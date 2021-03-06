﻿using System;
using DBTrie.Storage;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Reflection.Emit;
using System.Diagnostics;

namespace DBTrie.TrieModel
{
	internal class LTrieNode
	{
		LTrie Trie { get; }
		public int MinKeyLength { get; }

		internal LTrieNode(LTrie trie, int minKeyLength, in LTrieNodeStruct nodeStruct)
		{
			Trie = trie;
			MinKeyLength = minKeyLength;
			OwnPointer = nodeStruct.OwnPointer;
			LineLength = Sizes.DefaultPointerLen + (nodeStruct.ExternalLinkSlotCount * Sizes.ExternalLinkLength);
			externalLinks = new SortedList<byte, Link>(nodeStruct.ExternalLinkSlotCount);

			if (nodeStruct.InternalLinkPointer != 0)
				InternalLink = nodeStruct.GetInternalLinkObject();

			if (nodeStruct.FirstExternalLink.Pointer == 0)
				FreeSlotPointers.Enqueue(nodeStruct.FirstExternalLink.OwnPointer);
			else
				externalLinks.Add(nodeStruct.FirstExternalLink.Value, nodeStruct.FirstExternalLink.ToLinkObject());
		}
		internal LTrieNode(LTrie trie, int minKeyLength, in LTrieNodeStruct nodeStruct, ReadOnlySpan<byte> nextExternalLinks)
			: this(trie, minKeyLength, nodeStruct)
		{
			Debug.Assert(nextExternalLinks.Length == (nodeStruct.ExternalLinkSlotCount - 1) * Sizes.ExternalLinkLength);
			ReadExternalLinks(nodeStruct.GetSecondExternalLinkOwnPointer(), nextExternalLinks);
		}
		internal LTrieNode(LTrie trie, int minKeyLength, long pointer, ReadOnlyMemory<byte> memory)
		{
			Trie = trie;
			MinKeyLength = minKeyLength;
			OwnPointer = pointer;
			var span = memory.Span;
			ushort lineLen = span.ReadUInt16BigEndian();
			LineLength = span.Length - 2;
			var internalLinkPointer = (long)span.Slice(2).BigEndianToLongDynamic();
			if (internalLinkPointer != 0)
			{
				InternalLink = new Link(null)
				{
					Pointer = internalLinkPointer,
					LinkToNode = false,
					OwnPointer = OwnPointer + 2
				};
			}
			span = span.Slice(2 + Sizes.DefaultPointerLen, lineLen - Sizes.DefaultPointerLen);
			externalLinks = new SortedList<byte, Link>(span.Length / Sizes.ExternalLinkLength);
			ReadExternalLinks(OwnPointer + 2 + Sizes.DefaultPointerLen, span);
		}

		private void ReadExternalLinks(long firstSlotPointer, ReadOnlySpan<byte> span)
		{
			for (int j = 0; j < span.Length; j += Sizes.ExternalLinkLength)
			{
				var slotPointer = firstSlotPointer + j;
				var i = span[j];
				var linkPointer = (long)span.Slice(j + 2).BigEndianToLongDynamic();
				if (linkPointer == 0 || GetLink(i) is Link)
				{
					FreeSlotPointers.Enqueue(slotPointer);
					continue;
				}
				var l = new Link(i);
				l.Pointer = linkPointer;
				l.OwnPointer = slotPointer;
				l.LinkToNode = span[j + 1] == 0;
				externalLinks.Add(i, l);
			}
		}

		SortedList<byte, Link> externalLinks;
		public Link? GetLink(byte value)
		{
			externalLinks.TryGetValue(value, out var k);
			return k;
		}

		Queue<long>? _FreeSlotPointers;
		public Queue<long> FreeSlotPointers
		{
			get
			{
				if (_FreeSlotPointers is null)
					_FreeSlotPointers = new Queue<long>();
				return _FreeSlotPointers;
			}
		}

		public Link UpdateInternalLink(bool linkToNode, long pointer)
		{
			if (InternalLink is Link l)
			{
				l.Pointer = pointer;
				l.LinkToNode = false;
				l.OwnPointer = OwnPointer + 2;
			}
			else
			{
				l = new Link(null)
				{
					Pointer = pointer,
					LinkToNode = false,
					OwnPointer = OwnPointer + 2
				};
				InternalLink = l;
			}
			return l;
		}
		public Link UpdateExternalLink(long ownPointer, byte label, bool linkToNode, long pointer)
		{
			if (GetLink(label) is Link l)
			{
				l.OwnPointer = ownPointer;
				l.LinkToNode = linkToNode;
				l.Pointer = pointer;
			}
			else
			{
				l = new Link(label)
				{
					OwnPointer = ownPointer,
					LinkToNode = linkToNode,
					Pointer = pointer
				};
				externalLinks.Add(label, l);
			}
			return l;
		}

		public Link GetLinkFromPointer(long pointer)
		{
			return this.ExternalLinks.First(l => l.Pointer == pointer);
		}

		public IList<Link> ExternalLinks => externalLinks.Values;

		public int LineLength { get; internal set; }

		public Link? InternalLink { get; private set; }
		public long OwnPointer { get; internal set; }
		public int Size => LineLength + 2;

		internal async ValueTask SetInternalValue(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
		{
			if (InternalLink is Link l && await Trie.TryOverwriteValue(l, value))
				return;
			// Write value in storage
			var linkPointer = await WriteNewValue(key, value);
			// Update internal link
			await Trie.StorageHelper.WritePointer(OwnPointer + 2, linkPointer);
			// Update in-memory representation
			UpdateInternalLink(false, linkPointer);
		}

		internal async ValueTask<Link> SetValueLinkToNode(byte label)
		{
			if (this.GetLink(label) is Link l)
			{
				if (l.LinkToNode)
					throw new InvalidOperationException("Another link already exists");
				using var record = await Trie.ReadValue(l.Pointer);
				var internalLink = record.Key.Length == MinKeyLength + 1;
				var newNodePointer = await WriteNewNode(internalLink ? 1 : 2);
				if (internalLink)
				{
					await Trie.StorageHelper.WritePointer(newNodePointer + 2, record.Pointer);
				}
				// Update the link in storage
				await Trie.StorageHelper.WriteExternalLink(l.OwnPointer, label, true, newNodePointer);
				if (!internalLink)
				{
					var nextLabel = record.Key.Span[this.MinKeyLength + 1];
					await Trie.StorageHelper.WriteExternalLink(newNodePointer + 2 + Sizes.DefaultPointerLen, nextLabel, false, record.Pointer);
				}

				// Update in-memory
				l.LinkToNode = true;
				l.Pointer = newNodePointer;
				// We don't need to update the in-memory representation of the new node
				// because it currently is not in memory
				return l;
			}
			throw new InvalidOperationException("The specified link in SetValueLinkToNode should be a value node");
		}

		internal static int WriteNew(Span<byte> output, int neededSlots)
		{
			var reservedSlots = LTrieNode.GetSlotReservationCount(neededSlots);
			var nodeSize = GetNodeSize(reservedSlots);
			output.ToBigEndian((ushort)(nodeSize - 2));
			output.Slice(2).ToBigEndianDynamic(0);
			output.Slice(2 + Sizes.DefaultPointerLen, reservedSlots * Sizes.ExternalLinkLength).Fill(0);
			return nodeSize;
		}

		public static int GetSize(int neededSlots)
		{
			var reservedSlots = LTrieNode.GetSlotReservationCount(neededSlots);
			return 2 + Sizes.DefaultPointerLen + (reservedSlots * Sizes.ExternalLinkLength);
		}


		private async ValueTask<long> WriteNewNode(int neededSlots)
		{
			var reservedSlots = LTrieNode.GetSlotReservationCount(neededSlots);
			var nodeSize = GetNodeSize(reservedSlots);
			using var owner = Trie.MemoryPool.Rent(nodeSize);
			WriteNew(owner.Memory.Span, neededSlots);
			return await Trie.Storage.WriteToEnd(owner.Memory.Slice(0, nodeSize));
		}

		private static int GetNodeSize(int reservedSlots)
		{
			return 2 + Sizes.DefaultPointerLen + (reservedSlots * Sizes.ExternalLinkLength);
		}

		internal async ValueTask<bool> SetExternalValue(byte label, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
		{
			if (this.GetLink(label) is Link k)
			{
				if (await Trie.TryOverwriteValue(k, value))
					return false;
				// Write value in storage
				var valuePointer = await WriteNewValue(key, value);
				//Update the external link in storage
				await this.Trie.StorageHelper.WriteExternalLink(k.OwnPointer, label, false, valuePointer);
				// Update in-memory representation
				k.LinkToNode = false;
				k.Pointer = valuePointer;
				return false;
			}
			else
			{
				bool relocated = false;
				if (!this.FreeSlotPointers.TryPeek(out var emptySlotPointer))
				{
					// We need to relocate the current node somewhere else to
					// increase the number of slots
					relocated = true;
					await Relocate(externalLinks.Count + 1);
					emptySlotPointer = FreeSlotPointers.Peek();
				}

				// Let's add the new external link in one of the slot
				{
					// Write value in storage
					var valuePointer = await WriteNewValue(key, value);
					//Update the link in storage
					await Trie.StorageHelper.WriteExternalLink(emptySlotPointer, label, false, valuePointer);
					// Update in-memory representation
					this.FreeSlotPointers.Dequeue();
					this.UpdateExternalLink(emptySlotPointer, label, false, valuePointer);
				}
				return relocated;
			}
		}

		/// <summary>
		/// Used by tests to make sure there is no difference
		/// between storage and in-memory representation
		/// of the trie
		/// </summary>
		/// <returns></returns>
		internal async ValueTask AssertConsistency()
		{
			if (!Trie.ConsistencyCheck)
				return;
			var stored = await Trie.ReadNode(OwnPointer, MinKeyLength, false);
			if (stored.externalLinks.Count != externalLinks.Count)
				throw new Exception("stored.links.Count != links.Count");
			if (stored.InternalLink?.Pointer != InternalLink?.Pointer)
				throw new Exception("stored.InternalLink?.RecordPointer != InternalLink?.RecordPointer");
			for (int i = 0; i < 256; i++)
			{
				var k1 = stored.GetLink((byte)i);
				var k2 = GetLink((byte)i);
				if (k1?.OwnPointer != k2?.OwnPointer)
					throw new Exception("stored?.SlotPointer != this?.SlotPointer");
				if (k1?.Pointer != k2?.Pointer)
					throw new Exception("stored?.RecordPointer != this?.RecordPointer");
				if (k1?.LinkToNode != k2?.LinkToNode)
					throw new Exception("stored?.LinkToNode != this?.LinkToNode");
			}
		}

		private async Task Relocate(int neededSlots)
		{
			var newSlotCount = GetSlotReservationCount(neededSlots);
			var lineLen = Sizes.DefaultPointerLen + (newSlotCount * Sizes.ExternalLinkLength);
			using var owner = Trie.MemoryPool.Rent(2 + lineLen);
			int offset = 0;
			owner.Memory.Span.ToBigEndian((ushort)lineLen);
			offset += 2;
			if (InternalLink?.Pointer is long recordPointer)
				owner.Memory.Span.Slice(offset).ToBigEndianDynamic((ulong)recordPointer);
			else
				owner.Memory.Span.Slice(offset).ToBigEndianDynamic(0);
			offset += Sizes.DefaultPointerLen;
			foreach (var externalLink in ExternalLinks.OrderBy(k => k.OwnPointer))
			{
				owner.Memory.Span[offset] = externalLink.Label!.Value;
				owner.Memory.Span[offset + 1] = (byte)(externalLink.LinkToNode ? 0 : 1);
				offset += 2;
				owner.Memory.Span.Slice(offset).ToBigEndianDynamic((ulong)externalLink.Pointer);
				offset += Sizes.DefaultPointerLen;
			}
			owner.Memory.Span.Slice(offset).Fill(0);
			var newNodePointer = await Trie.Storage.WriteToEnd(owner.Memory.Slice(0, 2 + lineLen));
			// Update in-memory representation
			RelocateInMemory(newNodePointer);
			for (long location = OwnPointer + 2 + LineLength;
				location < OwnPointer + 2 + lineLen;
				location += Sizes.ExternalLinkLength)
			{
				FreeSlotPointers.Enqueue(location);
			}
			LineLength = lineLen;
		}

		internal async ValueTask<bool> SetValue(byte? linkLabel, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
		{
			if (linkLabel is byte b)
				return await SetExternalValue(b, key, value);
			await SetInternalValue(key, value);
			return false;
		}

		private void RelocateInMemory(long newPointer)
		{
			var oldPointer = OwnPointer;
			var offset = newPointer - OwnPointer;
			OwnPointer += offset;
			foreach (var k in ExternalLinks)
			{
				k.OwnPointer += offset;
			}
			Trie.NodeCache?.Relocate(oldPointer, newPointer);
		}

		public static int GetSlotReservationCount(int kl)
		{
			if (kl > 256)
				throw new ArgumentOutOfRangeException(nameof(kl), "Label count should be maximum 256");
			if (kl < 2) return 1;
			if (kl == 2) return 2;
			if (kl < 5) return 4;
			if (kl < 9) return 8;
			if (kl < 17) return 16;
			if (kl < 33) return 32;
			if (kl < 65) return 64;
			if (kl < 129) return 128;
			return 256;
		}

		internal async ValueTask<long> WriteNewValue(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value)
		{
			using var owner = Trie.MemoryPool.Rent(LTrieValue.GetSize(key.Length, value.Length, value.Length));
			var len = LTrieValue.WriteToSpan(owner.Memory.Span, key.Span, value.Span);
			var memory = owner.Memory.Slice(0, len);
			return await Trie.Storage.WriteToEnd(memory);
		}

		public async ValueTask<bool> RemoveInternalLink()
		{
			if (this.InternalLink is Link l)
			{
				await Trie.StorageHelper.WritePointer(l.OwnPointer, 0);
				InternalLink = null;
				return true;
			}
			return false;
		}
		public async ValueTask<bool> RemoveExternalLink(byte label)
		{
			if (this.GetLink(label) is Link l)
			{
				await Trie.StorageHelper.WriteExternalLink(l.OwnPointer, 0, false, 0);
				this.externalLinks.Remove(label);
				return true;
			}
			return false;
		}

		public Link? GetRemainingValueLink()
		{
			if (InternalLink is Link)
				return null;
			if (externalLinks.Count != 1)
				return null;
			var lastChild = externalLinks.Values.First();
			if (lastChild.LinkToNode)
				return null;
			return lastChild;
		}
	}
}

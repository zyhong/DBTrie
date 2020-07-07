using DBTrie.Storage;
using DBTrie.TrieModel;
using NuGet.Frameworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace DBTrie.Tests
{
	public class UnitTest1
	{
		private readonly ITestOutputHelper logs;

		public UnitTest1(ITestOutputHelper logs)
		{
			this.logs = logs;
		}

		[Fact]
		public async Task CanDoBasicTrieOperations()
		{
			CreateEmptyFile("Empty2", 0);
			await using var fs = new FileStorage("Empty2");
			var cache = new CacheStorage(fs, false);
			var trie = await LTrie.InitTrie(cache);
			await cache.Flush();
			trie = await ReloadTrie(trie);
			Assert.Null(await trie.GetValue(1 + "test" + 1));
			for (int i = 0; i < 5; i++)
			{
				await trie.SetKey(i + "test" + i, "lol" + i);
				Assert.Equal("lol" + i, await trie.GetValue(i + "test" + i));
			}
			for (int i = 0; i < 5; i++)
			{
				Assert.Equal("lol" + i, await trie.GetValue(i + "test" + i));
			}
			trie = await LTrie.OpenFromStorage(fs);
			for (int i = 0; i < 5; i++)
			{
				Assert.Null(await trie.GetValue(i + "test" + i));
			}
			await cache.Flush();
			trie = await ReloadTrie(trie);
			for (int i = 0; i < 5; i++)
			{
				Assert.Equal("lol" + i, await trie.GetValue(i + "test" + i));
			}
		}

		class TrieTester : IAsyncDisposable
		{
			public FileStorage Fs;
			public LTrie Trie;
			string file;
			public TrieTester(string file, LTrie trie, FileStorage fs)
			{
				this.Trie = trie;
				this.Fs = fs;
				this.file = file;
			}

			public static async Task<TrieTester> Create([CallerMemberName] string? caller = null)
			{
				caller ??= "unnamed";
				var fs = new FileStorage(caller);
				var trie = await LTrie.InitTrie(fs);
				trie.ConsistencyCheck = true;
				return new TrieTester(caller, trie, fs);
			}
			public async ValueTask DisposeAsync()
			{
				await Fs.DisposeAsync();
				File.Delete(file);
			}
		}

		[Fact]
		public async Task CanDelete()
		{
			await using (var t = await TrieTester.Create())
			{
				await t.Trie.SetKey("test", "lol");
				Assert.Equal("lol", await t.Trie.GetValue("test"));
				Assert.True(await t.Trie.DeleteRow("test"));
				Assert.False(await t.Trie.DeleteRow("test"));
				Assert.Null(await t.Trie.GetValue("test"));
			}
			await using (var t = await TrieTester.Create())
			{
				await t.Trie.SetKey("test", "lol1");
				await t.Trie.SetKey("test2", "lol2");
				Assert.Equal(2, t.Trie.RecordCount);
				Assert.True(await t.Trie.DeleteRow("test"));
				Assert.False(await t.Trie.DeleteRow("test"));
				Assert.Null(await t.Trie.GetValue("test"));
				Assert.Equal("lol2", await t.Trie.GetValue("test2"));
				Assert.Equal(1, t.Trie.RecordCount);
			}
			await using (var t = await TrieTester.Create())
			{
				await t.Trie.SetKey("test", "lol1");
				await t.Trie.SetKey("test2", "lol2");
				Assert.True(await t.Trie.DeleteRow("test2"));
				Assert.False(await t.Trie.DeleteRow("test2"));
				Assert.Null(await t.Trie.GetValue("test2"));
				Assert.Equal("lol1", await t.Trie.GetValue("test"));
			}
			await using (var t = await TrieTester.Create())
			{
				Assert.Equal(0, t.Trie.RecordCount);
				await t.Trie.SetKey("test1", "lol1");
				await t.Trie.SetKey("test2", "lol2");
				Assert.False(await t.Trie.DeleteRow("test"));
				Assert.Equal(2, t.Trie.RecordCount);
				Assert.True(await t.Trie.DeleteRow("test1"));
				Assert.Null(await t.Trie.GetValue("test1"));
				Assert.Equal("lol2", await t.Trie.GetValue("test2"));
				Assert.False(await t.Trie.DeleteRow("test1"));
				Assert.True(await t.Trie.DeleteRow("test2"));
				Assert.Null(await t.Trie.GetValue("test2"));
				Assert.Equal(0, t.Trie.RecordCount);
			}
		}

		[Fact]
		public async Task CacheTests()
		{
			CreateEmptyFile("Empty", 1030);
			await using var fs = new FileStorage("Empty");
			var cache = new CacheStorage(fs, pageSize: 128);
			await fs.Write(125, "abcdefgh");
			Assert.Equal("abcdefgh", await cache.Read(125, "abcdefgh".Length));
			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));
			await cache.Write(127, "CDEF");
			Assert.Equal("abCDEFgh", await cache.Read(125, "abCDEFgh".Length));
			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));

			Assert.Equal(2, cache.pages.Count);
			Assert.Equal(1030, fs.Length);
			Assert.Equal(1030, cache.Length);
			await cache.WriteToEnd("helloworld");
			Assert.Equal(1030 + "helloworld".Length, cache.Length);
			Assert.Equal("helloworld", await cache.Read(1030, "helloworld".Length));
			await cache.WriteToEnd("abdwuqiwiw");

			Assert.NotEqual("helloworld", await fs.Read(1030, "helloworld".Length));

			Assert.Equal("abcdefgh", await fs.Read(125, "abcdefgh".Length));
			await cache.Flush();
			Assert.Equal("helloworld", await fs.Read(1030, "helloworld".Length));
			Assert.Equal("abCDEFgh", await fs.Read(125, "abCDEFgh".Length));

			Assert.Equal(cache.Length, fs.Length);
			Assert.Equal(1030 + "helloworldabdwuqiwiw".Length, fs.Length);
			await fs.Reserve(10);
			Assert.Equal(1030 + "helloworldabdwuqiwiw".Length + 10, fs.Length);
		}

		private static void CreateEmptyFile(string name, int size)
		{
			if (File.Exists(name))
				File.Create(name).Close();
			var file = File.Create(name);
			if (size != 0)
			{
				file.Seek(size - 1, SeekOrigin.Begin);
				file.WriteByte(0);
			}
			file.Dispose();
		}

		[Fact]
		public async Task GeneralTests()
		{
			foreach (bool allowTrieCache in new[] { false, true })
				foreach (bool cacheStorageLayer in new[] { true, false })
				{
					await using var fs = CreateFileStorage("_DBreezeSchema", cacheStorageLayer);
					var trie = await CreateTrie(fs, allowTrieCache);
					var generationNode = await trie.ReadNode();
					var result = await trie.GetKey("@@@@LastFileNumber");
					Assert.NotNull(result);
					Assert.Equal(64, result!.Pointer);
					Assert.Equal(89, result.ValuePointer);
					Assert.Equal(8, result.ValueLength);
					Assert.Null(await trie.GetKey("notexists"));
					Assert.Null(await trie.GetKey("@u"));
					Assert.Equal(4282, trie.RecordCount);

					var schema = await Schema.OpenFromTrie(trie);
					Assert.True(await schema.TableExists("IndexProgress"));
					Assert.False(await schema.TableExists("In"));
					Assert.False(await schema.TableExists("IndexProgresss"));
					Assert.False(await schema.TableExists("IndexProgres"));

					var filename = await schema.GetFileNameOrCreate("IndexProgress");
					Assert.Equal(10000006UL, filename);
					Assert.Equal(10004281UL, schema.LastFileNumber);

					// This should create a new record
					filename = await schema.GetFileNameOrCreate("NotExists");
					Assert.Equal(10004282UL, filename);
					Assert.Equal(10004282UL, schema.LastFileNumber);
					Assert.Equal(4283, trie.RecordCount);

					// This should NOT create a new record
					filename = await schema.GetFileNameOrCreate("NotExists");
					Assert.Equal(10004282UL, filename);
					Assert.Equal(10004282UL, schema.LastFileNumber);
					Assert.Equal(4283, trie.RecordCount);

					// Reloading the tree
					trie = await ReloadTrie(trie);

					// We should get back our created table
					filename = await schema.GetFileNameOrCreate("NotExists");
					Assert.Equal(10004282UL, filename);
					Assert.Equal(10004282UL, schema.LastFileNumber);
					Assert.Equal(4283, trie.RecordCount);

					// Let's make sure this has been persisted as well
					schema = await Schema.OpenFromTrie(trie);
					Assert.Equal(10004282UL, schema.LastFileNumber);

					// Can list tables by name?
					schema = await Schema.OpenFromTrie(trie);
					var tables = await schema.GetTables("TestTa").ToArrayAsync();
					var ordered = tables.OrderBy(o => o).ToArray();
					Assert.True(tables.SequenceEqual(ordered));
					Assert.Equal(4, tables.Length);
					tables = await schema.GetTables("TestT").ToArrayAsync();
					ordered = tables.OrderBy(o => o).ToArray();
					Assert.True(tables.SequenceEqual(ordered));
					Assert.Equal(4, tables.Length);
					tables = await schema.GetTables("TestTab").ToArrayAsync();
					ordered = tables.OrderBy(o => o).ToArray();
					Assert.True(tables.SequenceEqual(ordered));
					Assert.Equal(3, tables.Length);
					Assert.NotNull(await trie.GetRow("@utTestTa"));

					await AssertMatch(trie, false, "POFwoinfOWu");
					await AssertMatch(trie, false, "@utTestT");
					await AssertMatch(trie, true, "@utTestTa");
					await AssertMatch(trie, true, "@utIndexProg");
					await AssertMatch(trie, true, "@utIndexProgT");
					await AssertMatch(trie, true, "@utIndexProgressss");
					await AssertMatch(trie, true, "@utIndexProgresa");

					tables = await schema.GetTables().ToArrayAsync();
					Assert.Equal(4282, tables.Length);
					ordered = tables.OrderBy(o => o).ToArray();
					Assert.True(tables.SequenceEqual(ordered));
					var r = new Random(0);
					for (int i = 0; i < 10; i++)
					{
						var keys = new string[3];
						keys[0] = RandomWord(5, r);
						keys[1] = keys[0] + RandomWord(1, r);
						keys[2] = keys[1] + RandomWord(1, r);
						var fromShortest = keys.ToArray();
						r.Shuffle(keys);

						// Try adding tables with intermediates
						var recordCountBefore = trie.RecordCount;
						foreach (var k in keys)
							await schema.GetFileNameOrCreate(k);
						tables = await schema.GetTables(fromShortest[0]).ToArrayAsync();
						ordered = tables.OrderBy(o => o).ToArray();
						Assert.True(tables.SequenceEqual(ordered));
						Assert.Equal(keys.Length, tables.Length);
						Assert.Equal(recordCountBefore + keys.Length, trie.RecordCount);
						tables = await schema.GetTables(fromShortest[1]).ToArrayAsync();
						Assert.Equal(keys.Length - 1, tables.Length);

						// Reloading
						trie = await ReloadTrie(trie);
						schema = await Schema.OpenFromTrie(trie);
						// Make sure our tables are still here
						foreach (var k in keys)
							Assert.True(await schema.TableExists(k));
						tables = await schema.GetTables(fromShortest[0]).ToArrayAsync();
						Assert.Equal(keys.Length, tables.Length);
						Assert.Equal(recordCountBefore + keys.Length, trie.RecordCount);
					}
				}
		}

		[Fact]
		public async Task CanListTransactions()
		{
			foreach (bool allowTrieCache in new[] { false })
				foreach (bool cacheStorageLayer in new[] { true })
				{
					logs.WriteLine($"allowTrieCache: {allowTrieCache}");
					logs.WriteLine($"cacheStorageLayer: {cacheStorageLayer}");
					await using var fs = CreateFileStorage("10000007", cacheStorageLayer);
					LTrie trie = await CreateTrie(fs, allowTrieCache);
					trie.ConsistencyCheck = false;
					DateTimeOffset now = DateTimeOffset.UtcNow;
					int records = 0;
					await foreach (var row in trie.EnumerateStartWith(""))
					{
						records++;
					}
					logs.WriteLine($"Record count : {records}");
					logs.WriteLine($"Enumerate 1 time : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
					now = DateTimeOffset.UtcNow;
					await foreach (var row in trie.EnumerateStartWith(""))
					{

					}
					logs.WriteLine($"Enumerate 2 time : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
					now = DateTimeOffset.UtcNow;
					await foreach (var row in trie.EnumerateStartWith(""))
					{
						using var owner = trie.MemoryPool.Rent(row.ValueLength);
						await trie.Storage.Read(row.ValuePointer, owner.Memory.Slice(row.ValueLength));
					}
					logs.WriteLine($"Enumerate values : {(int)(DateTimeOffset.UtcNow - now).TotalMilliseconds} ms");
				}
		}

		[Fact]
		public async Task CanSetKeyValue()
		{
			foreach (bool allowTrieCache in new[] { false, true })
				foreach (bool cacheStorageLayer in new[] { true, false })
				{
					await using var fs = CreateFileStorage("_DBreezeSchema", cacheStorageLayer);
					LTrie trie = await CreateTrie(fs, allowTrieCache);
					var countBefore = trie.RecordCount;
					Assert.Null(await trie.GetKey("CanSetKeyValue"));
					await trie.SetKey("CanSetKeyValue", "CanSetKeyValue-r1");
					Assert.Equal("CanSetKeyValue-r1", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal(countBefore + 1, trie.RecordCount);
					await trie.SetKey("CanSetKeyValue", "CanSetKeyValue-r2");
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal(countBefore + 1, trie.RecordCount);
					trie = await ReloadTrie(trie);
					Assert.Equal(countBefore + 1, trie.RecordCount);
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));

					Assert.Null(await trie.GetKey("Relocation"));
					await trie.SetKey("Relocation", "a");
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal(countBefore + 2, trie.RecordCount);

					Assert.Null(await trie.GetKey("NoRelocation"));
					await trie.SetKey("NoRelocation", "b");
					Assert.Equal("b", await trie.GetValue("NoRelocation"));
					Assert.Equal(countBefore + 3, trie.RecordCount);

					trie = await ReloadTrie(trie);
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal("b", await trie.GetValue("NoRelocation"));
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal(countBefore + 3, trie.RecordCount);

					Assert.Null(await trie.GetKey("k"));
					await trie.SetKey("k", "k-r1");
					Assert.Equal("k-r1", await trie.GetValue("k"));
					await trie.SetKey("k", "k-r2");
					Assert.Equal("k-r2", await trie.GetValue("k"));
					Assert.Equal(countBefore + 4, trie.RecordCount);

					Assert.Null(await trie.GetKey("CanSetKeyValue-Extended"));
					await trie.SetKey("CanSetKeyValue-Extended", "CanSetKeyValue-Extended-r1");
					Assert.Equal("CanSetKeyValue-Extended-r1", await trie.GetValue("CanSetKeyValue-Extended"));
					await trie.SetKey("CanSetKeyValue-Extended", "CanSetKeyValue-Extended-r2");
					Assert.Equal(countBefore + 5, trie.RecordCount);

					Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal("k-r2", await trie.GetValue("k"));
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal("b", await trie.GetValue("NoRelocation"));

					trie = await ReloadTrie(trie);

					Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal("k-r2", await trie.GetValue("k"));
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal("b", await trie.GetValue("NoRelocation"));
					Assert.Equal(countBefore + 5, trie.RecordCount);

					List<string> insertedKeys = new List<string>();
					Random r = new Random(0);
					for (int i = 0; i < 100; i++)
					{
						countBefore = trie.RecordCount;
						var keys = new string[5];
						int o = 0;
						var startWith = r.PickRandom(new[] {
						"@ut",
						"@",
						"k",
						"CanSetKeyValue",
						"CanSetKeyValueee",
						"CanSetKeyValue-Extended",
						"Relo",
						"Relocationn",
						"R",
						"NoRelocation",
						"" });
						keys[o++] = startWith + RandomWord(5, r);
						keys[o++] = keys[o - 2] + RandomWord(1, r);
						keys[o++] = keys[o - 2] + RandomWord(1, r);
						keys[o++] = keys[o - 2] + RandomWord(1, r);
						keys[o++] = keys[o - 2] + RandomWord(1, r);
						var fromShortest = keys.ToArray();
						r.Shuffle(keys);

						foreach (var k in keys)
						{
							Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
							if (i == 42)
							{
							}
							await trie.SetKey(k, k);
							Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
							Assert.Equal(k, await trie.GetValue(k));
							insertedKeys.Add(k);
						}
						foreach (var k in keys)
						{
							Assert.Equal(k, await trie.GetValue(k));
						}
						Assert.Equal(countBefore + keys.Length, trie.RecordCount);
					}
					countBefore = trie.RecordCount;
					// Everything kept value
					foreach (var k in insertedKeys)
					{
						Assert.Equal(k, await trie.GetValue(k));
					}
					// Randomly edit stuff
					HashSet<string> edited = new HashSet<string>();
					foreach (var k in insertedKeys)
					{
						if (r.Next() % 2 == 0)
						{
							await trie.SetKey(k, k + "-r2");
							edited.Add(k);
						}
					}
					// Everything kept value
					foreach (var k in insertedKeys)
					{
						var expected = edited.Contains(k) ? k + "-r2" : k;
						Assert.Equal(expected, await trie.GetValue(k));
					}

					// Randomly trucate
					HashSet<string> truncated = new HashSet<string>();
					foreach (var k in insertedKeys)
					{
						if (r.Next() % 2 == 0)
						{
							await trie.SetKey(k, k.GetHashCode().ToString());
							truncated.Add(k);
						}
					}

					// Randomly delete
					HashSet<string> deleted = new HashSet<string>();
					foreach (var k in insertedKeys)
					{
						if (r.Next() % 2 == 0)
						{
							Assert.True(await trie.DeleteRow(k));
							//Assert.True(await trie.DeleteRow(k), $"Failed to delete {k}");
							deleted.Add(k);
						}
					}

					// Everything kept value
					foreach (var k in insertedKeys)
					{
						if (deleted.Contains(k))
						{
							Assert.Null(await trie.GetRow(k));
						}
						else
						{
							var expected =
							truncated.Contains(k) ? k.GetHashCode().ToString() :
							edited.Contains(k) ? k + "-r2" : k;
							Assert.Equal(expected, await trie.GetValue(k));
						}
					}

					countBefore -= deleted.Count;
					Assert.Equal(countBefore, trie.RecordCount);
					// Nothing else got edited
					Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal("k-r2", await trie.GetValue("k"));
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal("b", await trie.GetValue("NoRelocation"));

					// Reload the trie
					trie = await ReloadTrie(trie);
					// Everything kept value
					foreach (var k in insertedKeys)
					{
						if (deleted.Contains(k))
						{
							Assert.Null(await trie.GetRow(k));
						}
						else
						{
							var expected =
							truncated.Contains(k) ? k.GetHashCode().ToString() :
							edited.Contains(k) ? k + "-r2" : k;
							Assert.Equal(expected, await trie.GetValue(k));
						}
					}
					Assert.Equal(countBefore, trie.RecordCount);
					// Nothing else got edited
					Assert.Equal("CanSetKeyValue-Extended-r2", await trie.GetValue("CanSetKeyValue-Extended"));
					Assert.Equal("CanSetKeyValue-r2", await trie.GetValue("CanSetKeyValue"));
					Assert.Equal("k-r2", await trie.GetValue("k"));
					Assert.Equal("a", await trie.GetValue("Relocation"));
					Assert.Equal("b", await trie.GetValue("NoRelocation"));
				}
		}

		private static async ValueTask<LTrie> ReloadTrie(LTrie trie)
		{
			var cache = trie.Storage as CacheStorage;
			var trie2 = await CreateTrie(trie.Storage, trie.NodeCache is { });
			trie2.ConsistencyCheck = trie.ConsistencyCheck;
			return trie2;
		}

		private static async ValueTask<LTrie> CreateTrie(IStorage fs, bool allowGenerationNodeCache)
		{
			var trie = await LTrie.OpenFromStorage(fs);
			trie.ConsistencyCheck = true;
			if (allowGenerationNodeCache)
				trie.ActivateCache();
			return trie;
		}

		private async Task AssertMatch(LTrie trie, bool linkToValue, string search)
		{
			var result = await trie.FindBestMatch(Encoding.UTF8.GetBytes(search));
			Assert.Equal(linkToValue, result.ValueLink is Link);
		}

		private string RandomWord(int minSize, Random r)
		{
			var alphabet = new[] { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j' };
			var count = r.Next(minSize, 10);
			return new string(Enumerable.Range(0, count)
				.Select(_ => r.PickRandom(alphabet))
				.ToArray());
		}

		private IStorage CreateFileStorage(string file, bool cacheStorageLayer, [CallerMemberName] string? caller = null)
		{
			if (caller is null)
				throw new ArgumentNullException(nameof(caller));
			Directory.CreateDirectory(caller);
			File.Copy($"Data/{file}", $"{caller}/{file}", true);
			var fs = new FileStorage($"{caller}/{file}");
			if (!cacheStorageLayer)
				return fs;
			return new CacheStorage(fs);
		}
	}
}

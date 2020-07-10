# DBTrie

This project is a complete rewrite of [DBreeze](https://github.com/hhblaze/DBreeze), which is a managed implementation of a key/value store based on [radix tree](https://en.wikipedia.org/wiki/Radix_tree).

The initial DBreeze project, while working well, is barely maintained and were showing performance issues for my use cases.

## How to use

This is a basic key value store.

### How to insert

```csharp
static async Task Main(string args)
{
	Directory.CreateDirectory("Db");
	await using (var engine = await DBTrieEngine.OpenFromFolder("Db"))
	{
		var tx = await engine.OpenTransaction();
		var table = tx.GetTable("MyTable");
		await table.Insert("MyKey", "MyValue");
		await tx.Commit();
	}
}
```

### How to delete

```csharp
static async Task Main(string args)
{
	Directory.CreateDirectory("Db");
	await using (var engine = await DBTrieEngine.OpenFromFolder("Db"))
	{
		// ....
		var table = tx.GetTable("MyTable");
		table.Delete("MyKey");
		tx.Commit();
		// ...
	}
}
```

### How to enumerate

```csharp
static async Task Main(string args)
{
	Directory.CreateDirectory("Db");
	await using (var engine = await DBTrieEngine.OpenFromFolder("Db"))
	{
		// ...
		var table = tx.GetTable("MyTable");
		await foreach (var item in table.Enumerate(startsWith: "Ke"))
		{
			using (item)
			{
				// Do things...
			}
		}
	}
	// ....
}
```

### How to get an item

```csharp
static async Task Main(string args)
{
	Directory.CreateDirectory("Db");
	await using (var engine = await DBTrieEngine.OpenFromFolder("Db"))
	{
		// ....
		var table = tx.GetTable("MyTable");
		using (var row = await table.Get("MyKey"))
		{
			// Do things...
		}
		// ...
	}
}
```

### How to defragment your table

When data is deleted, DBTrie does not immediately free the RAM and storage that was consumed by this data.
For this you need to defragment your tables once in a while.

This operation is iterating over all the saved values, so you should not use it too often and might be quite lengthy.
```csharp
static async Task Main(string args)
{
	Directory.CreateDirectory("Db");
	await using (var engine = await DBTrieEngine.OpenFromFolder("Db"))
	{
		// ....
		var table = tx.GetTable("MyTable");
		await table.Defragment()
		// ...
	}
}
```

## Best practices

* The API allows the use of `[ReadOnly]Memory<byte>` extensively to limit allocations. If you are writting performance sensitive code, consider using those.
* Think of disposing the `IRow` you get from a table. This decrease the pressure on the garbage collector.
* Think of disposing the `Transaction` you get from the engine.
* Do not create many tables, each table is backed by a file on disk.
* Defragment your table once in a while to decrease RAM and storage consumption.

## Todo

* Currently, only a single transaction can be processed at same time. It should be possible to allow concurrent transactions if those do not use the same tables.
* We cache the underlying file of each table in-memory. We should evict the least used pages to save memory.

## License

MIT
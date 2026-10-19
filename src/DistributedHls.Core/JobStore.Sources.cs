using Microsoft.Data.Sqlite;

namespace DistributedHls.Core;

public sealed partial class JobStore
{
    private const string SourceColumns = "item_id,library_id,path,length,modified_ticks,sha256";

    // One result per file, in input order. Path, length or modification time changing means new content: the hash is cleared.
    public IReadOnlyList<UpsertResult> UpsertSources(IReadOnlyList<SourceFile> files) => Write(cmd =>
    {
        var results = new List<UpsertResult>(files.Count);
        foreach (var file in files)
        {
            (string, object?)[] values = [("$item", Id(file.ItemId)), ("$library", Id(file.LibraryId)), ("$path", file.Path), ("$length", file.Length), ("$ticks", file.LastWriteTimeUtc.Ticks)];
            var stored = Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources WHERE item_id=$item", values), ReadSource).SingleOrDefault();
            if (stored is null)
            {
                Sql(cmd, "INSERT INTO sources(item_id,library_id,path,length,modified_ticks) VALUES($item,$library,$path,$length,$ticks)", values).ExecuteNonQuery();
                results.Add(new(file.ItemId, SourceChange.Inserted, null));
            }
            else if (stored.File.Path == file.Path && stored.File.Length == file.Length && stored.File.LastWriteTimeUtc.Ticks == file.LastWriteTimeUtc.Ticks)
            {
                if (stored.File.LibraryId != file.LibraryId) Sql(cmd, "UPDATE sources SET library_id=$library WHERE item_id=$item", values).ExecuteNonQuery();
                results.Add(new(file.ItemId, SourceChange.Unchanged, stored.Sha256));
            }
            else
            {
                Sql(cmd, "UPDATE sources SET library_id=$library,path=$path,length=$length,modified_ticks=$ticks,sha256=NULL WHERE item_id=$item", values).ExecuteNonQuery();
                results.Add(new(file.ItemId, SourceChange.ContentChanged, stored.Sha256));
            }
        }
        return results;
    });

    public string? RemoveSource(Guid itemId) => Write(cmd =>
    {
        var sha = Sql(cmd, "SELECT sha256 FROM sources WHERE item_id=$item", ("$item", Id(itemId))).ExecuteScalar() as string;
        Sql(cmd, "DELETE FROM sources WHERE item_id=$item", ("$item", Id(itemId))).ExecuteNonQuery();
        return sha;
    });

    public StoredSource? GetSource(Guid itemId) =>
        Read(cmd => Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources WHERE item_id=$item", ("$item", Id(itemId))), ReadSource).SingleOrDefault());

    public IReadOnlyList<StoredSource> Sources() => Read(cmd => Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources ORDER BY item_id"), ReadSource));

    public IReadOnlyList<StoredSource> SourcesBySha(string sha) =>
        Read(cmd => Rows(Sql(cmd, $"SELECT {SourceColumns} FROM sources WHERE sha256=$sha ORDER BY item_id", ("$sha", sha)), ReadSource));

    private static StoredSource ReadSource(SqliteDataReader r) =>
        new(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt64(3), new DateTime(r.GetInt64(4), DateTimeKind.Utc)), Text(r, 5));
}

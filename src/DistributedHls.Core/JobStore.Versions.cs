namespace DistributedHls.Core;

public sealed partial class JobStore
{
    public IReadOnlyDictionary<int, (Guid Id, string Signature)> VersionItems(Guid itemId) => Read(cmd => Rows(Sql(cmd,
        "SELECT quality,version_id,signature FROM version_items WHERE item_id=$item", ("$item", Id(itemId))),
        r => (Quality: r.GetInt32(0), Id: Guid.Parse(r.GetString(1)), Signature: r.GetString(2))).ToDictionary(v => v.Quality, v => (v.Id, v.Signature)));

    public void SetVersionItem(Guid itemId, int q, Guid versionId, string signature) => Write(cmd => Sql(cmd, """
        INSERT INTO version_items(item_id,quality,version_id,signature) VALUES($item,$quality,$version,$signature)
        ON CONFLICT(item_id,quality) DO UPDATE SET version_id=excluded.version_id,signature=excluded.signature
        """, ("$item", Id(itemId)), ("$quality", q), ("$version", Id(versionId)), ("$signature", signature)).ExecuteNonQuery());

    public void RemoveVersionItem(Guid itemId, int q) =>
        Write(cmd => Sql(cmd, "DELETE FROM version_items WHERE item_id=$item AND quality=$quality", ("$item", Id(itemId)), ("$quality", q)).ExecuteNonQuery());

    public IReadOnlyList<Guid> ItemsWithVersions() =>
        Read(cmd => Rows(Sql(cmd, "SELECT DISTINCT item_id FROM version_items ORDER BY item_id"), r => Guid.Parse(r.GetString(0))));

    public IReadOnlyList<Guid> LegacyItems() => Read(cmd => Rows(Sql(cmd, "SELECT item_id FROM legacy_items ORDER BY item_id"), r => Guid.Parse(r.GetString(0))));

    public void RemoveLegacyItem(Guid itemId) => Write(cmd => Sql(cmd, "DELETE FROM legacy_items WHERE item_id=$item", ("$item", Id(itemId))).ExecuteNonQuery());
}

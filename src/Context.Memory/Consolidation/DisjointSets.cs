namespace Context.Memory.Consolidation;

/// <summary>
/// Union-find data structure with union by size and path halving.
/// </summary>
internal sealed class DisjointSets
{
    private readonly int[] parent;
    private readonly int[] size;

    public DisjointSets(int count)
    {
        parent = [.. Enumerable.Range(0, count)];
        size = [.. Enumerable.Repeat(1, count)];
    }

    public int Find(int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    public int SizeOf(int x) => size[Find(x)];

    public void Union(int a, int b)
    {
        var (ra, rb) = (Find(a), Find(b));
        if (ra == rb)
        {
            return;
        }

        if (size[ra] < size[rb])
        {
            (ra, rb) = (rb, ra);
        }

        parent[rb] = ra;
        size[ra] += size[rb];
    }

    /// <summary>Members of each set, each list ascending, lists ordered by their smallest member.</summary>
    public List<List<int>> Groups() =>
        [.. Enumerable.Range(0, parent.Length).GroupBy(Find).Select(g => g.Order().ToList()).OrderBy(g => g[0])];
}

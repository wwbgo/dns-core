using DnsCore.Models;

namespace DnsCore.Services;

/// <summary>
/// 本地多值 A/AAAA 记录的轮询排序。权重不同时按权重轮询，
/// 权重相同时按查询次数轮换；DNS 客户端通常优先使用 answer 区第一条记录。
/// </summary>
public static class DnsRecordOrderer
{
    public static List<DnsRecord> Rotate(IReadOnlyList<DnsRecord> records, int offset)
    {
        ArgumentNullException.ThrowIfNull(records);

        var copy = records.ToList();
        if (copy.Count <= 1)
            return copy;

        var normalized = ((offset % copy.Count) + copy.Count) % copy.Count;
        if (normalized == 0)
            return copy;

        return copy.Skip(normalized)
            .Concat(copy.Take(normalized))
            .ToList();
    }

    /// <summary>
    /// 权重全部相同时沿用普通轮询；否则按权重轮询选择首条记录。
    /// </summary>
    public static List<DnsRecord> OrderForQuery(IReadOnlyList<DnsRecord> records, int sequence)
    {
        ArgumentNullException.ThrowIfNull(records);

        var copy = records.ToList();
        if (copy.Count <= 1)
            return copy;

        var firstWeight = EffectiveWeight(copy[0]);
        return copy.All(r => EffectiveWeight(r) == firstWeight)
            ? Rotate(copy, sequence)
            : WeightedRoundRobin(copy, sequence);
    }

    /// <summary>
    /// 简单加权轮询：把 sequence 映射到权重累计区间，选中的记录放到首位。
    /// </summary>
    public static List<DnsRecord> WeightedRoundRobin(IReadOnlyList<DnsRecord> records, int sequence)
    {
        ArgumentNullException.ThrowIfNull(records);

        var copy = records.ToList();
        if (copy.Count <= 1)
            return copy;

        var weights = copy.Select(EffectiveWeight).ToArray();
        var totalWeight = weights.Sum();
        if (totalWeight <= 0)
            return copy;

        var target = ((sequence % totalWeight) + totalWeight) % totalWeight;
        var selected = 0;
        var cumulative = 0;

        for (var i = 0; i < weights.Length; i++)
        {
            cumulative += weights[i];
            if (target < cumulative)
            {
                selected = i;
                break;
            }
        }

        var ordered = new List<DnsRecord>(copy.Count) { copy[selected] };
        ordered.AddRange(copy.Take(selected));
        ordered.AddRange(copy.Skip(selected + 1));
        return ordered;
    }

    private static int EffectiveWeight(DnsRecord record)
        => Math.Clamp(record.Weight, 1, 1000);
}

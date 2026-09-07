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

        if (records.Count <= 1)
            return [.. records];

        var normalized = ((offset % records.Count) + records.Count) % records.Count;
        var rotated = new List<DnsRecord>(records.Count);

        for (var i = 0; i < records.Count; i++)
            rotated.Add(records[(normalized + i) % records.Count]);

        return rotated;
    }

    /// <summary>
    /// 权重全部相同时沿用普通轮询；否则按权重轮询选择首条记录。
    /// </summary>
    public static List<DnsRecord> OrderForQuery(IReadOnlyList<DnsRecord> records, int sequence)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count <= 1)
            return [.. records];

        var firstWeight = EffectiveWeight(records[0]);

        for (var i = 1; i < records.Count; i++)
        {
            if (EffectiveWeight(records[i]) != firstWeight)
                return WeightedRoundRobin(records, sequence);
        }

        return Rotate(records, sequence);
    }

    /// <summary>
    /// 简单加权轮询：把 sequence 映射到权重累计区间，选中的记录放到首位。
    /// </summary>
    public static List<DnsRecord> WeightedRoundRobin(IReadOnlyList<DnsRecord> records, int sequence)
    {
        ArgumentNullException.ThrowIfNull(records);

        var result = new List<DnsRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
            result.Add(records[i]);

        if (result.Count <= 1)
            return result;

        var weights = new int[result.Count];
        var totalWeight = 0;

        for (var i = 0; i < result.Count; i++)
        {
            weights[i] = EffectiveWeight(result[i]);
            totalWeight += weights[i];
        }

        if (totalWeight <= 0)
            return result;

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

        var ordered = new List<DnsRecord>(result.Count) { result[selected] };
        for (var i = 0; i < selected; i++)
            ordered.Add(result[i]);
        for (var i = selected + 1; i < result.Count; i++)
            ordered.Add(result[i]);

        return ordered;
    }

    private static int EffectiveWeight(DnsRecord record)
        => Math.Clamp(record.Weight, 1, 1000);
}

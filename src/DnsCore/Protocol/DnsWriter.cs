using DnsCore.Models;
using System.Net;
using System.Text;

namespace DnsCore.Protocol;

/// <summary>
/// DNS 线格式写入器：支持域名压缩（RFC 1035 §4.1.4）、
/// 严格的 label/域名长度校验，以及各记录类型的 RDATA 编码。
/// </summary>
public sealed class DnsWriter
{
    private byte[] _buffer;
    private int _position;

    /// <summary>域名 -> 报文内偏移，用于生成压缩指针</summary>
    private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.OrdinalIgnoreCase);

    public DnsWriter(int initialCapacity = 512)
        => _buffer = new byte[Math.Max(initialCapacity, DnsHeader.Size)];

    public int Position => _position;

    private void Ensure(int additional)
    {
        var required = _position + additional;
        if (required <= _buffer.Length)
            return;

        var newSize = Math.Max(_buffer.Length * 2, required);
        Array.Resize(ref _buffer, Math.Min(newSize, DnsLimits.MaxMessageSize + 2));

        if (_position + additional > _buffer.Length)
            throw new InvalidOperationException("DNS 报文超出最大长度");
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)(value & 0xFF);
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        _buffer[_position++] = (byte)(value >> 24);
        _buffer[_position++] = (byte)(value >> 16);
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)(value & 0xFF);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
    }

    /// <summary>在指定偏移回填一个 16 位值（用于 RDLENGTH）</summary>
    public void PatchUInt16(int offset, ushort value)
    {
        if (offset < 0 || offset > _position - 2)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _buffer[offset] = (byte)(value >> 8);
        _buffer[offset + 1] = (byte)(value & 0xFF);
    }

    public void WriteHeader(DnsHeader header)
    {
        Ensure(DnsHeader.Size);
        header.WriteTo(_buffer.AsSpan(_position, DnsHeader.Size));
        _position += DnsHeader.Size;
    }

    /// <summary>
    /// 回填报文头（计数与标志位在写完答案后才最终确定）
    /// </summary>
    public void PatchHeader(DnsHeader header)
    {
        if (_position < DnsHeader.Size)
            throw new InvalidOperationException("报文头尚未写入");

        header.WriteTo(_buffer.AsSpan(0, DnsHeader.Size));
    }

    /// <summary>
    /// 回滚到指定位置，用于放弃写入一条记录（编码失败或超出报文上限）。
    /// 同时丢弃该位置之后登记的压缩偏移，否则会生成指向已回滚区域的悬空指针。
    /// </summary>
    public void Rewind(int position)
    {
        if (position < 0 || position > _position)
            throw new ArgumentOutOfRangeException(nameof(position));

        foreach (var key in _nameOffsets.Where(kv => kv.Value >= position).Select(kv => kv.Key).ToList())
            _nameOffsets.Remove(key);

        _position = position;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _position).ToArray();

    // ==== 域名编码 ====

    /// <summary>
    /// 写入域名。useCompression 为 true 时对已出现过的后缀生成压缩指针。
    /// </summary>
    public void WriteDomainName(string domain, bool useCompression = true)
    {
        if (string.IsNullOrEmpty(domain) || domain == ".")
        {
            WriteByte(0);
            return;
        }

        var name = domain.TrimEnd('.');
        ValidateDomainName(name);

        var labels = name.Split('.');

        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels, i, labels.Length - i);

            // 压缩指针只能表示 14 位偏移
            if (useCompression && _nameOffsets.TryGetValue(suffix, out var offset) && offset <= 0x3FFF)
            {
                WriteUInt16((ushort)(0xC000 | offset));
                return;
            }

            if (useCompression && _position <= 0x3FFF)
                _nameOffsets[suffix] = _position;

            var labelBytes = Encoding.ASCII.GetBytes(labels[i]);
            WriteByte((byte)labelBytes.Length);
            WriteBytes(labelBytes);
        }

        WriteByte(0);
    }

    /// <summary>
    /// 校验域名：label 非空、不超 63 字节、线格式总长不超 255。
    /// 原实现直接 (byte)length 强转，300 字符的 label 会被截成 44 并静默产出损坏报文。
    /// </summary>
    /// <param name="strictCharset">
    /// 是否强制 RFC 1035 主机名字符集（字母、数字、连字符）。
    /// 仅在 API 写入等入口开启：应答编码路径还要处理 PTR 的 in-addr.arpa、
    /// 下划线开头的 SRV 名（_sip._tcp）以及上游返回的域名，过严会误伤。
    /// </param>
    public static void ValidateDomainName(string domain, bool strictCharset = false)
    {
        var name = domain.TrimEnd('.');
        if (name.Length == 0)
            return;

        var wireLength = 1; // 结尾的 0 字节
        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0)
                throw new ArgumentException($"域名含空 label: {domain}", nameof(domain));

            var byteCount = Encoding.ASCII.GetByteCount(label);
            if (byteCount > DnsLimits.MaxLabelLength)
                throw new ArgumentException(
                    $"域名 label 超长（{byteCount} > {DnsLimits.MaxLabelLength}）: {label}", nameof(domain));

            if (strictCharset)
                ValidateLabelCharset(label, domain);

            wireLength += byteCount + 1;
        }

        if (wireLength > DnsLimits.MaxDomainNameLength)
            throw new ArgumentException(
                $"域名超长（{wireLength} > {DnsLimits.MaxDomainNameLength} 字节）: {domain}", nameof(domain));
    }

    /// <summary>
    /// 校验单个 label 的字符集（RFC 1035 preferred name syntax，另放行下划线）。
    /// 拦掉引号、尖括号等字符：它们无法出现在合法主机名中，
    /// 却会被存入记录并回显到管理界面，构成注入载荷的来源。
    /// </summary>
    private static void ValidateLabelCharset(string label, string domain)
    {
        foreach (var c in label)
        {
            var ok = c is >= 'a' and <= 'z'
                  || c is >= 'A' and <= 'Z'
                  || c is >= '0' and <= '9'
                  || c == '-' || c == '_';

            if (!ok)
                throw new ArgumentException(
                    $"域名含非法字符 '{c}'（仅允许字母、数字、连字符）: {domain}", nameof(domain));
        }
    }
}

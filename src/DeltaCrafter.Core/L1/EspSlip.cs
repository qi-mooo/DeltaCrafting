namespace DeltaCrafter.Core.L1;

/// <summary>
/// ESP ROM 串口下载协议的 SLIP 分帧:帧以 0xC0 包围,帧内 0xC0→DB DC、0xDB→DB DD。
/// 与 esptool 的 slip_reader/write 行为一致。
/// </summary>
internal static class EspSlip
{
    private const byte End = 0xC0, Esc = 0xDB, EscEnd = 0xDC, EscEsc = 0xDD;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var output = new List<byte>(payload.Length + 8) { End };
        foreach (byte b in payload)
        {
            if (b == End) { output.Add(Esc); output.Add(EscEnd); }
            else if (b == Esc) { output.Add(Esc); output.Add(EscEsc); }
            else output.Add(b);
        }
        output.Add(End);
        return output.ToArray();
    }

    /// <summary>增量解码:逐字节喂入,完整一帧时返回帧内容。帧外噪声(如启动日志)被丢弃。</summary>
    public sealed class Decoder
    {
        private readonly List<byte> _frame = new();
        private bool _inFrame, _escaped;

        public byte[]? Push(byte b)
        {
            if (!_inFrame)
            {
                if (b == End) { _inFrame = true; _frame.Clear(); }
                return null;
            }
            if (_escaped)
            {
                _escaped = false;
                if (b == EscEnd) _frame.Add(End);
                else if (b == EscEsc) _frame.Add(Esc);
                else throw new InvalidDataException($"SLIP 转义序列无效: 0xDB 0x{b:X2}");
                return null;
            }
            if (b == Esc) { _escaped = true; return null; }
            if (b != End) { _frame.Add(b); return null; }
            // 连续两个 0xC0:前一帧结尾紧跟下一帧开头,空帧忽略。
            if (_frame.Count == 0) return null;
            var complete = _frame.ToArray();
            _frame.Clear();
            _inFrame = false;
            return complete;
        }

        public void Reset()
        {
            _frame.Clear();
            _inFrame = _escaped = false;
        }
    }
}

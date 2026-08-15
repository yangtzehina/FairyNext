// 移植自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Utils/ByteBuffer.cs（465 行）@ 08a2d56
//
// 本文件只活在编译器前端。发布版运行时不含 .fui 解析（平面五：运行期唯一输入是 FGB 冻结记录，
// 零解析零反射零字典）——.fui 的字段级跳转（两级块表 nextPos 相对跳转）留在这里，因为编辑器
// 与编译器是独立演进的两方，那正是字段级跳转该待的地方（架构文档「编译平面 · 核心数据结构」）。
//
// 相对 fork 的改动（每条都写了病因，不是风格改写）：
//  1. ReadColor：UnityEngine.Color32 → FairyNext.Numerics.Color32。fork 那行还顺手隐式转成了
//     浮点 Color，.fui 里存的就是 RGBA8，转浮点是纯损失。
//  2. ReadPath：去 GPathPoint 依赖 → 抛 NotSupportedException。路径数据只有 transition 的
//     path tween 用得上，本期（M1-12）不需要；随 M2 tween 引擎回归。
//  3. static byte[] temp（fork 第 33 行）→ 实例字段 _temp。静态暂存在并行编包下是数据竞争，
//     而这条读路径在小端机读大端 .fui 时**每个 float 都要走**（见 ReadFloat）。
//  4. 窗口边界检查（硬化，非风格）：fork 除 ReadBytes 外不校验 _length，越窗读只有越过整个
//     byte[] 才抛——.fui 是外部输入，越窗读会静默读到窗口之外的字节并当成合法数据。这里每个
//     读口先 Require(n)，越窗抛 FuiFormatException，由 FuiPackage.TryParse 收敛成「拒收 + 诊断」。
//     这是平面五机制 12（fuzz 纪律：任意字节序列输入，永不 panic、永不越界读）的前置条件。
//  5. ReadBuffer 的子缓冲起点：fork 写 new ByteBuffer(_data, _pointer, count)，漏加 _offset；
//     只有宿主缓冲 offset == 0 时才对（fork 的 UIPackage 恰好总是 0，所以一直没暴露）。
//     改为 _offset + _pointer。
//  6. ReadDouble 的字节交换分支：fork 写 BitConverter.ToSingle（笔误）。该分支在小端机读大端
//     .fui 时必走，只是 .fui 里没有 double 字段所以从未暴露。改为 ToDouble。
//  7. Seek（fork 427-461）的**算法原样保留**——两级块表是 .fui 前向兼容的核心机制。仅加窗口
//     校验：块表位置/条目/跳转目标越窗一律返回 false，与 fork 里 newPos <= 0 的「无此块」
//     信号走同一个出口。
//  8. 去掉 buffer 属性的 setter（fork 93-101：换数组顺带重置 offset/length）。窗口三元组
//     (data, offset, length) 成了不可变量，换数组请另建实例——改动 4 的边界检查以窗口不变为前提，
//     留一个能在读到一半换掉窗口的 setter 等于把刚关上的门再开一条缝。fork 内无调用点。

using System;
using System.Text;
using FairyNext.Numerics;

namespace FairyNext.Compiler.Fui
{
    /// <summary>
    /// .fui 字节流读取器。默认**大端**（fork 语义：littleEndian = false），字符串共享一张
    /// 包级字符串表（<see cref="ReadS"/> 的两个哨兵：65534 = null、65533 = 空串）。
    ///
    /// 边界语义：缓冲窗口是 [<c>bufferOffset</c>, <c>bufferOffset + length</c>)。窗口外的字节
    /// 一律读不到——越窗读抛 <see cref="FuiFormatException"/>，不静默返回相邻字节。
    /// <see cref="Skip"/> 与 <see cref="position"/> 的 setter 不校验（与 fork 一致：跳到窗外
    /// 合法，读才失败），这样「先跳到块尾再判断」的调用形态不必额外分支。
    /// </summary>
    public sealed class ByteBuffer
    {
        /// <summary>false = 大端。.fui 全程大端；本字段留着是因为 fork 的 fnt/位图字体子流会翻。</summary>
        public bool littleEndian;

        /// <summary>包级共享字符串表（<see cref="ReadS"/> 按下标取）。子缓冲继承同一引用。</summary>
        public string?[]? stringTable;

        /// <summary>.fui 描述符版本号（包头读出后写入；决定若干块的存在与字段数）。</summary>
        public int version;

        int _pointer;
        readonly int _offset;
        readonly int _length;
        readonly byte[] _data;

        readonly byte[] _temp = new byte[8];

        public ByteBuffer(byte[] data, int offset = 0, int length = -1)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || offset > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            _data = data;
            _pointer = 0;
            _offset = offset;
            if (length < 0)
                _length = data.Length - offset;
            else
                _length = length;

            // 写成减法而不是 _offset + _length > data.Length：后者在 length 接近 int.MaxValue 时
            // 会溢出成负数，把越界窗口放行。
            if (_length < 0 || _length > data.Length - _offset)
                throw new ArgumentOutOfRangeException(nameof(length));

            littleEndian = false;
        }

        /// <summary>窗口内的读写位置（相对 <see cref="bufferOffset"/>）。</summary>
        public int position
        {
            get { return _pointer; }
            set { _pointer = value; }
        }

        /// <summary>窗口长度（字节）。</summary>
        public int length
        {
            get { return _length; }
        }

        /// <summary>
        /// 本缓冲窗口在 <see cref="buffer"/> 中的起点。与 <see cref="length"/> 一起划定本缓冲
        /// 真正拥有的字节——底层数组可以更大、可以共享。
        /// </summary>
        public int bufferOffset
        {
            get { return _offset; }
        }

        public bool bytesAvailable
        {
            get { return _pointer < _length; }
        }

        /// <summary>底层数组（只读访问；换数组请另建 ByteBuffer——offset 是只读字段）。</summary>
        public byte[] buffer
        {
            get { return _data; }
        }

        /// <summary>越窗即抛。所有读口的唯一门。</summary>
        void Require(int count)
        {
            if (count < 0 || _pointer < 0 || _pointer > _length - count)
                throw new FuiFormatException(
                    $"读越窗：position={_pointer} 需要 {count} 字节，窗口长度 {_length}");
        }

        public int Skip(int count)
        {
            _pointer += count;
            return _pointer;
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[_offset + _pointer++];
        }

        public byte[] ReadBytes(byte[] output, int destIndex, int count)
        {
            Require(count);
            Array.Copy(_data, _offset + _pointer, output, destIndex, count);
            _pointer += count;
            return output;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            byte[] result = new byte[count];
            Array.Copy(_data, _offset + _pointer, result, 0, count);
            _pointer += count;
            return result;
        }

        /// <summary>读 int 长度前缀后切出子缓冲（共享底层数组、字符串表与 version）。</summary>
        public ByteBuffer ReadBuffer()
        {
            int count = ReadInt();
            Require(count);   // 长度前缀本身可能是垃圾：先验证再切，别切出一个越窗的窗口
            ByteBuffer ba = new ByteBuffer(_data, _offset + _pointer, count);
            ba.stringTable = stringTable;
            ba.version = version;
            ba.littleEndian = littleEndian;
            _pointer += count;
            return ba;
        }

        public char ReadChar()
        {
            return (char)ReadShort();
        }

        public bool ReadBool()
        {
            Require(1);
            bool result = _data[_offset + _pointer] == 1;
            _pointer++;
            return result;
        }

        public short ReadShort()
        {
            Require(2);
            int startIndex = _offset + _pointer;
            _pointer += 2;
            if (littleEndian)
                return (short)(_data[startIndex] | (_data[startIndex + 1] << 8));
            else
                return (short)((_data[startIndex] << 8) | _data[startIndex + 1]);
        }

        public ushort ReadUshort()
        {
            return (ushort)ReadShort();
        }

        public int ReadInt()
        {
            Require(4);
            int startIndex = _offset + _pointer;
            _pointer += 4;
            if (littleEndian)
                return (_data[startIndex]) | (_data[startIndex + 1] << 8) | (_data[startIndex + 2] << 16) | (_data[startIndex + 3] << 24);
            else
                return (_data[startIndex] << 24) | (_data[startIndex + 1] << 16) | (_data[startIndex + 2] << 8) | (_data[startIndex + 3]);
        }

        public uint ReadUint()
        {
            return (uint)ReadInt();
        }

        public float ReadFloat()
        {
            Require(4);
            int startIndex = _offset + _pointer;
            _pointer += 4;
            if (littleEndian == BitConverter.IsLittleEndian)
                return BitConverter.ToSingle(_data, startIndex);
            else
            {
                _temp[3] = _data[startIndex];
                _temp[2] = _data[startIndex + 1];
                _temp[1] = _data[startIndex + 2];
                _temp[0] = _data[startIndex + 3];
                return BitConverter.ToSingle(_temp, 0);
            }
        }

        public long ReadLong()
        {
            Require(8);
            int startIndex = _offset + _pointer;
            _pointer += 8;
            if (littleEndian)
            {
                int i1 = (_data[startIndex]) | (_data[startIndex + 1] << 8) | (_data[startIndex + 2] << 16) | (_data[startIndex + 3] << 24);
                int i2 = (_data[startIndex + 4]) | (_data[startIndex + 5] << 8) | (_data[startIndex + 6] << 16) | (_data[startIndex + 7] << 24);
                return (uint)i1 | ((long)i2 << 32);
            }
            else
            {
                int i1 = (_data[startIndex] << 24) | (_data[startIndex + 1] << 16) | (_data[startIndex + 2] << 8) | (_data[startIndex + 3]);
                int i2 = (_data[startIndex + 4] << 24) | (_data[startIndex + 5] << 16) | (_data[startIndex + 6] << 8) | (_data[startIndex + 7]);
                return (uint)i2 | ((long)i1 << 32);
            }
        }

        public double ReadDouble()
        {
            Require(8);
            int startIndex = _offset + _pointer;
            _pointer += 8;
            if (littleEndian == BitConverter.IsLittleEndian)
                return BitConverter.ToDouble(_data, startIndex);
            else
            {
                _temp[7] = _data[startIndex];
                _temp[6] = _data[startIndex + 1];
                _temp[5] = _data[startIndex + 2];
                _temp[4] = _data[startIndex + 3];
                _temp[3] = _data[startIndex + 4];
                _temp[2] = _data[startIndex + 5];
                _temp[1] = _data[startIndex + 6];
                _temp[0] = _data[startIndex + 7];
                return BitConverter.ToDouble(_temp, 0);   // 改动 6：fork 此处误写 ToSingle
            }
        }

        /// <summary>ushort 长度前缀 + UTF-8 字节（内联串，不走字符串表）。</summary>
        public string ReadString()
        {
            ushort len = ReadUshort();
            Require(len);
            string result = Encoding.UTF8.GetString(_data, _offset + _pointer, len);
            _pointer += len;
            return result;
        }

        public string ReadString(int len)
        {
            Require(len);
            string result = Encoding.UTF8.GetString(_data, _offset + _pointer, len);
            _pointer += len;
            return result;
        }

        /// <summary>
        /// 共享字符串表取串。两个哨兵是 .fui 格式的一部分：65534 = null（字段缺席），
        /// 65533 = 空串（字段在但内容为空）——两者语义不同，合并即丢信息。
        /// </summary>
        public string? ReadS()
        {
            int index = ReadUshort();
            if (index == 65534) //null
                return null;
            else if (index == 65533)
                return string.Empty;

            string?[]? table = stringTable;
            if (table == null || index >= table.Length)
                throw new FuiFormatException($"字符串表下标越界：index={index}，表长 {(table == null ? -1 : table.Length)}");
            return table[index];
        }

        public string?[] ReadSArray(int cnt)
        {
            if (cnt < 0) throw new FuiFormatException($"字符串数组长度为负：{cnt}");
            string?[] ret = new string?[cnt];
            for (int i = 0; i < cnt; i++)
                ret[i] = ReadS();

            return ret;
        }

        /// <summary>
        /// 改动 2：fork 在此读 GPathPoint 列表（transition 的 path tween）。本期不需要路径数据，
        /// 且 GPathPoint 依赖 Vector3/曲线类型；随 M2 tween 引擎回归。
        /// </summary>
        public void ReadPath()
        {
            throw new NotSupportedException(
                "ReadPath：路径数据（transition path tween）随 M2 tween 引擎回归；M1-12 前端不读它。");
        }

        /// <summary>
        /// 原地改写字符串表（fork 的 TranslationHelper 走这条）。FairyNext 的多语言走 STRT 不可变
        /// + LANG 补丁段的视图叠加（平面五机制 14），故运行时没有这条路径；它只留给编译器前端
        /// 消化 fork 的翻译文件。
        /// </summary>
        public void WriteS(string value)
        {
            int index = ReadUshort();
            if (index != 65534 && index != 65533)
            {
                string?[]? table = stringTable;
                if (table == null || index >= table.Length)
                    throw new FuiFormatException($"字符串表下标越界（写）：index={index}");
                table[index] = value;
            }
        }

        /// <summary>改动 1：RGBA8 直读，不转浮点。</summary>
        public Color32 ReadColor()
        {
            Require(4);
            int startIndex = _offset + _pointer;
            byte r = _data[startIndex];
            byte g = _data[startIndex + 1];
            byte b = _data[startIndex + 2];
            byte a = _data[startIndex + 3];
            _pointer += 4;

            return new Color32(r, g, b, a);
        }

        /// <summary>
        /// 两级块表跳转（fork 427-461，算法原样）。<paramref name="indexTablePos"/> 处是
        /// { u8 段数; u8 是否 short 偏移; (short|int)[段数] 相对偏移 }；偏移相对 indexTablePos。
        /// 返回 false = **本块不存在**（偏移 ≤ 0），调用方据此走默认值——.fui 的前向兼容就靠这个：
        /// 新版编辑器追加块，旧版读取器读不到就当缺席，不会错位。
        ///
        /// 改动 7：越窗（块表位置、条目、跳转目标任一越出本缓冲窗口）与「块不存在」走同一出口，
        /// 返回 false 且**不移动** position。fork 在这些情形下会读到窗口外的字节。
        /// </summary>
        public bool Seek(int indexTablePos, int blockIndex)
        {
            int tmp = _pointer;
            if (indexTablePos < 0 || indexTablePos >= _length || blockIndex < 0)
                return false;

            _pointer = indexTablePos;
            int segCount = _data[_offset + _pointer++];
            if (blockIndex < segCount)
            {
                if (_pointer >= _length) { _pointer = tmp; return false; }
                bool useShort = _data[_offset + _pointer++] == 1;
                int newPos;
                if (useShort)
                {
                    _pointer += 2 * blockIndex;
                    if (_pointer < 0 || _pointer > _length - 2) { _pointer = tmp; return false; }
                    newPos = ReadShort();
                }
                else
                {
                    _pointer += 4 * blockIndex;
                    if (_pointer < 0 || _pointer > _length - 4) { _pointer = tmp; return false; }
                    newPos = ReadInt();
                }

                if (newPos > 0)
                {
                    int target = indexTablePos + newPos;
                    if (target < 0 || target > _length) { _pointer = tmp; return false; }
                    _pointer = target;
                    return true;
                }
                else
                {
                    _pointer = tmp;
                    return false;
                }
            }
            else
            {
                _pointer = tmp;
                return false;
            }
        }
    }
}

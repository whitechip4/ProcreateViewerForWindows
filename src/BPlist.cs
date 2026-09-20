using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProcreateViewer
{
    /// <summary>NSKeyedArchiver object reference (plist UID type).</summary>
    public sealed class PlistUid
    {
        public readonly int Index;
        public PlistUid(int index) { Index = index; }
        public override string ToString() { return "UID(" + Index + ")"; }
    }

    /// <summary>
    /// Reader for Apple binary property lists ("bplist00"), sufficient for NSKeyedArchiver output.
    /// Objects are materialised lazily: ints -> long, reals -> double, strings -> string, data -> byte[],
    /// arrays/sets -> List&lt;object&gt;, dicts -> Dictionary&lt;string, object&gt;, UIDs -> PlistUid.
    /// </summary>
    public sealed class BPlist
    {
        private readonly byte[] b;
        private readonly int offSize, refSize;
        private readonly long[] offsets;
        private readonly object[] cache;
        private readonly bool[] done;
        public readonly int TopIndex;

        public BPlist(byte[] data)
        {
            b = data;
            if (b.Length < 40 || b[0] != (byte)'b' || b[1] != (byte)'p' || b[2] != (byte)'l' || b[3] != (byte)'i' || b[4] != (byte)'s' || b[5] != (byte)'t')
                throw new InvalidDataException("not a binary plist");
            int t = b.Length - 32;
            offSize = b[t + 6];
            refSize = b[t + 7];
            long num = ReadBE(t + 8, 8);
            TopIndex = (int)ReadBE(t + 16, 8);
            long table = ReadBE(t + 24, 8);
            offsets = new long[num];
            for (int i = 0; i < num; i++) offsets[i] = ReadBE((int)(table + (long)i * offSize), offSize);
            cache = new object[num];
            done = new bool[num];
        }

        public object Top { get { return Get(TopIndex); } }

        public object Get(int index)
        {
            if (done[index]) return cache[index];
            done[index] = true;              // guards against malformed self references
            object o = Parse((int)offsets[index]);
            cache[index] = o;
            return o;
        }

        private long ReadBE(int pos, int size)
        {
            long v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | b[pos + i];
            return v;
        }

        private int ReadCount(ref int pos, int info)
        {
            if (info != 0xF) return info;
            int m = b[pos++];
            int size = 1 << (m & 0xF);
            long v = ReadBE(pos, size);
            pos += size;
            return (int)v;
        }

        private object Parse(int pos)
        {
            int marker = b[pos++];
            int type = marker >> 4, info = marker & 0xF;
            switch (type)
            {
                case 0x0:
                    if (info == 0x8) return false;
                    if (info == 0x9) return true;
                    return null;
                case 0x1:
                    {
                        int size = 1 << info;
                        if (size == 16) return ReadBE(pos + 8, 8);
                        return ReadBE(pos, size);   // 8-byte ints are signed, shorter ones unsigned; both fall out of the shift
                    }
                case 0x2:
                    if (info == 2) { byte[] f = new byte[4]; Array.Copy(b, pos, f, 0, 4); Array.Reverse(f); return (double)BitConverter.ToSingle(f, 0); }
                    if (info == 3) { byte[] d = new byte[8]; Array.Copy(b, pos, d, 0, 8); Array.Reverse(d); return BitConverter.ToDouble(d, 0); }
                    return 0.0;
                case 0x3:
                    {
                        byte[] d = new byte[8]; Array.Copy(b, pos, d, 0, 8); Array.Reverse(d);
                        return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.ToDouble(d, 0));
                    }
                case 0x4:
                    { int n = ReadCount(ref pos, info); byte[] d = new byte[n]; Array.Copy(b, pos, d, 0, n); return d; }
                case 0x5:
                    { int n = ReadCount(ref pos, info); return Encoding.ASCII.GetString(b, pos, n); }
                case 0x6:
                    { int n = ReadCount(ref pos, info); return Encoding.BigEndianUnicode.GetString(b, pos, n * 2); }
                case 0x8:
                    return new PlistUid((int)ReadBE(pos, info + 1));
                case 0xA:
                case 0xC:
                    {
                        int n = ReadCount(ref pos, info);
                        var list = new List<object>(n);
                        for (int i = 0; i < n; i++) list.Add(Get((int)ReadBE(pos + i * refSize, refSize)));
                        return list;
                    }
                case 0xD:
                    {
                        int n = ReadCount(ref pos, info);
                        var dict = new Dictionary<string, object>(n);
                        for (int i = 0; i < n; i++)
                        {
                            object k = Get((int)ReadBE(pos + i * refSize, refSize));
                            object v = Get((int)ReadBE(pos + (n + i) * refSize, refSize));
                            string ks = k as string;
                            dict[ks ?? Convert.ToString(k)] = v;
                        }
                        return dict;
                    }
                default:
                    throw new InvalidDataException("unsupported plist object type 0x" + type.ToString("X"));
            }
        }
    }

    /// <summary>Convenience layer over an NSKeyedArchiver plist ($objects table + $top.root).</summary>
    public sealed class NsArchive
    {
        public readonly List<object> Objects;
        public readonly Dictionary<string, object> Root;

        public NsArchive(byte[] data)
        {
            var pl = new BPlist(data);
            var top = pl.Top as Dictionary<string, object>;
            if (top == null || !top.ContainsKey("$objects") || !top.ContainsKey("$top")) throw new InvalidDataException("not an NSKeyedArchiver plist");
            Objects = (List<object>)top["$objects"];
            var t = (Dictionary<string, object>)top["$top"];
            Root = Dict(t["root"]);
            if (Root == null) throw new InvalidDataException("archive root is not a dictionary");
        }

        public object Resolve(object v)
        {
            var u = v as PlistUid;
            if (u == null) return v;
            object o = Objects[u.Index];
            string s = o as string;
            if (s != null && s == "$null") return null;
            return o;
        }

        public Dictionary<string, object> Dict(object v) { return Resolve(v) as Dictionary<string, object>; }
        public string Str(object v) { return Resolve(v) as string; }

        public object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? Resolve(v) : null;
        }

        public string StrKey(Dictionary<string, object> d, string key) { return Get(d, key) as string; }

        public string ClassName(Dictionary<string, object> d)
        {
            var cd = Get(d, "$class") as Dictionary<string, object>;
            object n;
            if (cd != null && cd.TryGetValue("$classname", out n)) return n as string;
            return null;
        }

        public List<object> NsObjects(Dictionary<string, object> d, string key)
        {
            var arr = Get(d, key) as Dictionary<string, object>;
            object l;
            if (arr != null && arr.TryGetValue("NS.objects", out l)) return l as List<object>;
            return null;
        }

        public static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            if (v is bool) return (bool)v ? 1 : 0;
            return def;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            if (v is long) return (long)v != 0;
            return def;
        }
    }
}

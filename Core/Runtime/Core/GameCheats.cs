using System;
using System.Collections.Generic;
using System.Threading;

namespace CSharpMrp
{
    [Serializable]
    public sealed class CheatPathKey
    {
        public string text = "";
        public int number;
        public bool numeric;
    }

    [Serializable]
    public sealed class CheatDefinition
    {
        public string name = "";
        public string backend = "arm";
        public uint address;
        public int width = 4;
        public CheatPathKey[] path = Array.Empty<CheatPathKey>();
        public long value;
        [NonSerialized]
        public long sessionId;
        public string Label => backend == "arm" ? "0x" + address.ToString("X8") : PathLabel();

        private string PathLabel()
        {
            string result = "_G";
            foreach (var key in path)
            {
                result += key.numeric ? "[" + key.number + "]" : "[\"" + key.text + "\"]";
            }
            return result;
        }

        public CheatDefinition Copy()
        {
            return new CheatDefinition
            {
                name = name, backend = backend, address = address, width = width,
                path = (CheatPathKey[])path.Clone(), value = value, sessionId = sessionId
            };
        }
    }

    public sealed class CheatResult
    {
        public CheatDefinition Target = new CheatDefinition();
        public long Value;
        public int FreezeId;
    }

    public sealed class CheatPage
    {
        public CheatResult[] Results = Array.Empty<CheatResult>();
        public int Total;
        public bool Limited;
        public bool Searching;
        public int Examined;
        public string Notice = "";
        public bool HasSearch;
    }

    public enum CheatComparison
    {
        Equal, Changed, Unchanged, Increased, Decreased
    }

    /// <summary>Owner-thread only. Bounded ARM searches preserve unsigned 16/32-bit target widths.</summary>
    public sealed class GameCheats : IDisposable
    {
        public const int MaximumCandidates = 262144;
        private readonly RuntimeSession session;
        private readonly Func<bool> allowed;
        private static long nextSession;
        private readonly long sessionId = Interlocked.Increment(ref nextSession);
        private struct ArmCandidate
        {
            public uint Address;
            public int Width;
            public uint Previous;
        }
        private sealed class Freeze
        {
            public int Id;
            public CheatDefinition Target = new CheatDefinition();
        }
        private List<ArmCandidate> arm = new List<ArmCandidate>();
        private readonly List<Freeze> freezes = new List<Freeze>();
        private IEnumerator<int>? search;
        private bool disposed;
        private int sequence;
        private bool hasSearch;
        public bool Searching => search != null;
        public bool Limited { get; private set; }
        public int Examined { get; private set; }
        public string Notice { get; private set; } = "";
        public int Count => arm.Count;

        public GameCheats(RuntimeSession session, Func<bool> allowed)
        {
            this.session = session;
            this.allowed = allowed;
        }

        private void Demand()
        {
            if (disposed || !session.Started)
            {
                throw new InvalidOperationException("游戏会话已结束，金手指目标已失效。");
            }
            if (!allowed())
            {
                freezes.Clear();
                Cancel();
                throw new UnauthorizedAccessException("金手指需要 PRO 权益。");
            }
        }

        public void Begin(long? value, bool refine, CheatComparison comparison)
        {
            Demand();
            if (refine && !hasSearch)
            {
                throw new InvalidOperationException("请先重新搜索。");
            }
            if (value.HasValue)
            {
                ValidateValue(value.Value);
            }
            if (refine && comparison == CheatComparison.Equal && !value.HasValue)
            {
                throw new ArgumentException("等于搜索需要输入数值。");
            }
            Cancel();
            hasSearch = true;
            Examined = 0;
            Notice = "";
            if (!refine)
            {
                Limited = false;
                arm.Clear();
            }
            search = SearchArm(value, refine, comparison).GetEnumerator();
        }

        public bool Step(CancellationToken cancellation = default)
        {
            Demand();
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                System.Diagnostics.Stopwatch.Frequency / 250;
            for (int work = 0; work < 32768 && search != null; work++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!search.MoveNext())
                {
                    Cancel();
                }
                if ((work & 255) == 255 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }
            }
            return Searching;
        }

        public void Cancel()
        {
            search?.Dispose();
            search = null;
        }

        private static bool Matches(long current, long previous, long? value, CheatComparison comparison)
        {
            switch (comparison)
            {
                case CheatComparison.Equal:
                    return !value.HasValue || current == value.Value;
                case CheatComparison.Changed:
                    return current != previous;
                case CheatComparison.Unchanged:
                    return current == previous;
                case CheatComparison.Increased:
                    return current > previous;
                case CheatComparison.Decreased:
                    return current < previous;
                default:
                    throw new ArgumentOutOfRangeException(nameof(comparison));
            }
        }

        private IEnumerable<int> SearchArm(long? value, bool refine, CheatComparison comparison)
        {
            var next = new List<ArmCandidate>();
            var ranges = session.CheatDataRanges();
            if (refine)
            {
                foreach (var candidate in arm)
                {
                    if (Contains(ranges, candidate.Address, candidate.Width))
                    {
                        uint current = Read(candidate.Address, candidate.Width);
                        if (Matches(current, candidate.Previous, value, comparison))
                        {
                            next.Add(new ArmCandidate { Address = candidate.Address, Width = candidate.Width, Previous = current });
                        }
                    }
                    Examined++;
                    yield return 0;
                }
            }
            else
            {
                foreach (var range in ranges)
                {
                    for (uint address = range.Start; (ulong)address + 2 <= range.End; address++)
                    {
                        // The same bytes may represent either a word or a packed halfword.
                        // Retain both interpretations: adjacent fields can change independently.
                        for (int width = 4; width >= 2; width -= 2)
                        {
                            if ((ulong)address + (uint)width > range.End
                                || width == 2 && value > ushort.MaxValue) continue;
                            uint current = Read(address, width);
                            if (!value.HasValue || current == value.Value)
                            {
                                if (next.Count == MaximumCandidates)
                                {
                                    Limited = true;
                                    arm = next;
                                    yield break;
                                }
                                next.Add(new ArmCandidate { Address = address, Width = width, Previous = current });
                            }
                            Examined++;
                            yield return 0;
                        }
                    }
                }
            }
            arm = next;
        }

        private uint Read(uint address, int width) => width == 2 ? session.Memory.U16(address) : session.Memory.U32(address);

        public static bool IsSupported(CheatDefinition target) => target.backend == "arm" && (target.width == 2 || target.width == 4);

        private static bool Contains(List<(uint Start, uint End)> ranges, uint address, int width)
        {
            foreach (var range in ranges)
            {
                if (address >= range.Start && (ulong)address + (uint)width <= range.End)
                {
                    return true;
                }
            }
            return false;
        }

        private static void ValidateValue(long value)
        {
            if (value < 0 || value > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "ARM 数值范围为 0 至 4294967295。");
            }
        }

        private void Validate(CheatDefinition target, List<(uint Start, uint End)>? ranges = null)
        {
            if (target.sessionId != 0 && target.sessionId != sessionId)
            {
                throw new InvalidOperationException("金手指目标属于已结束的游戏会话。");
            }
            if (!IsSupported(target))
            {
                throw new InvalidOperationException("仅支持 ARM 16/32 位条目，请重新搜索 ARM 内存。");
            }
            ValidateValue(target.value);
            if (target.width == 2 && target.value > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(target.value), "16 位数值范围为 0 至 65535。");
            }
            if (!Contains(ranges ?? session.CheatDataRanges(), target.address, target.width))
            {
                throw new InvalidOperationException("目标已失效或属于受保护的内存，请重新搜索。");
            }
        }

        public void Write(CheatDefinition definition)
        {
            Demand();
            Validate(definition);
            Store(definition);
        }

        public void CheckTarget(CheatDefinition definition)
        {
            Demand();
            Validate(definition);
        }

        private static bool SameTarget(CheatDefinition left, CheatDefinition right)
        {
            return left.backend == right.backend && left.address == right.address && left.width == right.width;
        }

        private void Store(CheatDefinition target)
        {
            if (target.width == 2) session.Memory.Put16(target.address, (ushort)target.value);
            else session.Memory.Put32(target.address, (uint)target.value);
        }

        public int FreezeValue(CheatDefinition definition)
        {
            Demand();
            Validate(definition);
            var pinned = definition.Copy();
            var existing = freezes.Find(item => SameTarget(item.Target, pinned));
            if (existing != null)
            {
                existing.Target = pinned;
                Store(pinned);
                return existing.Id;
            }
            if (freezes.Count >= 64)
            {
                throw new InvalidOperationException("同时锁定条目最多为 64 个。");
            }
            var item = new Freeze { Id = ++sequence, Target = pinned };
            Store(pinned);
            freezes.Add(item);
            return item.Id;
        }

        public void DisableFreeze(int id)
        {
            Demand();
            freezes.RemoveAll(item => item.Id == id);
        }

        public void ApplyFreezes()
        {
            if (disposed || !allowed())
            {
                if (freezes.Count > 0)
                {
                    Notice = "PRO 权益不可用，已停止全部锁定。";
                }
                freezes.Clear();
                Cancel();
                return;
            }
            if (Searching)
            {
                return;
            }
            List<(uint Start, uint End)>? ranges = null;
            for (int index = freezes.Count - 1; index >= 0; index--)
            {
                try
                {
                    var item = freezes[index];
                    ranges ??= session.CheatDataRanges();
                    Validate(item.Target, ranges);
                    Store(item.Target);
                }
                catch (Exception error) when (error is InvalidOperationException || error is RuntimeFault)
                {
                    Notice = error.Message;
                    freezes.RemoveAt(index);
                }
            }
        }

        public CheatPage Page(int offset, int count = 20)
        {
            Demand();
            if (offset < 0 || count < 1 || count > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            var rows = new List<CheatResult>();
            var ranges = session.CheatDataRanges();
            int end = (int)Math.Min(Count, (long)offset + count);
            for (int index = offset; index < end; index++)
            {
                var candidate = arm[index];
                if (!Contains(ranges, candidate.Address, candidate.Width))
                {
                    Notice = "本页存在已失效目标，请继续搜索以缩小结果。";
                    continue;
                }
                var target = new CheatDefinition
                {
                    address = candidate.Address, width = candidate.Width, sessionId = sessionId,
                    value = Read(candidate.Address, candidate.Width)
                };
                rows.Add(new CheatResult { Target = target, Value = target.value });
            }
            return new CheatPage
            {
                Results = rows.ToArray(), Total = Count, Limited = Limited, Searching = Searching,
                Examined = Examined, Notice = Notice, HasSearch = hasSearch
            };
        }

        public CheatResult[] Frozen()
        {
            Demand();
            var result = new CheatResult[freezes.Count];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = new CheatResult
                {
                    Target = freezes[index].Target.Copy(), Value = freezes[index].Target.value,
                    FreezeId = freezes[index].Id
                };
            }
            return result;
        }

        public void Dispose()
        {
            disposed = true;
            Cancel();
            arm.Clear();
            freezes.Clear();
        }
    }

    public sealed partial class GuestMemory
    {
        internal List<(uint Start, uint End)> CheatOccupiedRanges()
        {
            var free = new List<(uint Start, uint End)>();
            var visited = new HashSet<uint>();
            uint end = U32(HeapCells + 8);
            if (end < HeapBase || end > HeapEnd)
            {
                throw new InvalidOperationException("客体内存边界无效。");
            }
            uint limit = end - HeapBase;
            uint offset = U32(HeapHead);
            while (offset != limit)
            {
                if (visited.Count >= 65536 || !visited.Add(offset) || (offset & 3) != 0
                    || (ulong)offset + 8 > limit)
                {
                    throw new InvalidOperationException("客体内存分配信息不可用，无法安全搜索。");
                }
                uint length = U32(HeapBase + offset + 4);
                if (length < 8 || (ulong)offset + length > limit)
                {
                    throw new InvalidOperationException("客体空闲内存范围无效。");
                }
                free.Add((HeapBase + offset, HeapBase + offset + length));
                offset = U32(HeapBase + offset);
            }
            free.Sort((left, right) => left.Start.CompareTo(right.Start));
            var result = new List<(uint Start, uint End)>();
            uint cursor = HeapBase;
            foreach (var range in free)
            {
                if (range.Start > cursor)
                {
                    result.Add((cursor, range.Start));
                }
                cursor = Math.Max(cursor, range.End);
            }
            if (cursor < end)
            {
                result.Add((cursor, end));
            }
            return result;
        }
    }

    public sealed partial class RuntimeSession
    {
        internal List<(uint Start, uint End)> CheatDataRanges()
        {
            var ranges = Memory.CheatOccupiedRanges();
            var protectedRanges = new List<(uint Start, uint End)>
            {
                (Framebuffer, Framebuffer + framebufferCapacity)
            };
            foreach (var module in Modules)
            {
                protectedRanges.Add((module.Address, module.Address + module.Size));
            }
            foreach (var block in protectedRanges)
            {
                for (int index = ranges.Count - 1; index >= 0; index--)
                {
                    var range = ranges[index];
                    if (block.Start >= range.End || block.End <= range.Start)
                    {
                        continue;
                    }
                    ranges.RemoveAt(index);
                    if (range.Start < block.Start)
                    {
                        ranges.Insert(index, (range.Start, block.Start));
                    }
                    if (block.End < range.End)
                    {
                        ranges.Insert(index, (block.End, range.End));
                    }
                }
            }
            return ranges;
        }
    }
}

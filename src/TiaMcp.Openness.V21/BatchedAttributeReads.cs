#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Openness.V21
{
    // Bulk walks read a block's or tag's attributes in one GetAttributes call instead of one native round trip per
    // property (2.9x faster on a warm capture, docs/verification/batched-capture-reads-20261112.md). A native type whose
    // batch throws or returns unexpected values falls back to the typed properties for the rest of the worker's
    // lifetime, so a result never depends on the batch succeeding.
    internal static class BatchedAttributeReads
    {
        internal static readonly string[] BlockNames = { "Name", "Namespace", "Number", "ProgrammingLanguage", "IsConsistent", "IsKnowHowProtected", "ModifiedDate" };
        internal static readonly string[] TagNames = { "Name", "DataTypeName", "LogicalAddress", "IsSafety", "ExternalAccessible", "ExternalVisible", "ExternalWritable" };

        private static readonly ConcurrentDictionary<string, string> Fallbacks = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        internal sealed class BlockAttributes
        {
            internal string Name, Namespace, ProgrammingLanguage;
            internal int Number;
            internal bool IsConsistent, IsKnowHowProtected;
            internal DateTime ModifiedDate;
        }

        internal sealed class TagAttributes
        {
            internal string Name, DataTypeName, LogicalAddress;
            internal bool IsSafety, ExternalAccessible, ExternalVisible, ExternalWritable;
        }

        internal static BlockAttributes Block(PlcBlock block) => Read(block.GetType().FullName, () => block.GetAttributes(BlockNames), values =>
        {
            Expect(values, BlockNames.Length);
            return new BlockAttributes { Name = (string)values[0], Namespace = (string)values[1], Number = (int)values[2],
                ProgrammingLanguage = ((ProgrammingLanguage)values[3]).ToString(), IsConsistent = (bool)values[4],
                IsKnowHowProtected = (bool)values[5], ModifiedDate = (DateTime)values[6] };
        }, () => new BlockAttributes { Name = block.Name, Namespace = block.Namespace, Number = block.Number,
            ProgrammingLanguage = block.ProgrammingLanguage.ToString(), IsConsistent = block.IsConsistent,
            IsKnowHowProtected = block.IsKnowHowProtected, ModifiedDate = block.ModifiedDate });

        internal static TagAttributes Tag(PlcTag tag) => Read(tag.GetType().FullName, () => tag.GetAttributes(TagNames), values =>
        {
            Expect(values, TagNames.Length);
            return new TagAttributes { Name = (string)values[0], DataTypeName = (string)values[1], LogicalAddress = (string)values[2],
                IsSafety = (bool)values[3], ExternalAccessible = (bool)values[4], ExternalVisible = (bool)values[5], ExternalWritable = (bool)values[6] };
        }, () => new TagAttributes { Name = tag.Name, DataTypeName = tag.DataTypeName, LogicalAddress = tag.LogicalAddress, IsSafety = tag.IsSafety,
            ExternalAccessible = tag.ExternalAccessible, ExternalVisible = tag.ExternalVisible, ExternalWritable = tag.ExternalWritable });

        // typeKey: the native type, so one failing type does not disable batching for the others.
        internal static T Read<T>(string typeKey, Func<IList<object>> batch, Func<IList<object>, T> convert, Func<T> typed)
        {
            if (Fallbacks.ContainsKey(typeKey)) return typed();
            IList<object> values;
            try { values = batch(); }
            catch (Exception error) when (!IsCritical(error)) { Fallbacks.TryAdd(typeKey, error.GetType().Name); return typed(); }
            try { return convert(values); }
            catch (Exception error) when (error is InvalidCastException || error is NullReferenceException || error is ArgumentException)
            { Fallbacks.TryAdd(typeKey, "UnexpectedValues"); return typed(); }
        }

        internal static IReadOnlyDictionary<string, string> FallbackTypes => Fallbacks;

        internal static void ResetFallbacks() => Fallbacks.Clear();

        private static void Expect(IList<object> values, int count)
        {
            if (values == null || values.Count != count) throw new ArgumentException("GetAttributes returned " + (values?.Count.ToString() ?? "no") + " values for " + count + " names.");
        }

        private static bool IsCritical(Exception error) => error is OutOfMemoryException || error is StackOverflowException || error is AccessViolationException;
    }
}

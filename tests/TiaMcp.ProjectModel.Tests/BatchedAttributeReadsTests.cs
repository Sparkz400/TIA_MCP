using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{

    internal static class BatchedAttributeReadsTests
    {
        internal static Task BatchIsUsedAndTypedPropertiesAreNotRead()
        {
            BatchedAttributeReads.ResetFallbacks();
            var block = new PlcBlock();
            var values = BatchedAttributeReads.Block(block);
            Check(values.Name == "batched" && values.Namespace == "Ns" && values.Number == 7 && values.ProgrammingLanguage == "SCL"
                && values.IsConsistent && !values.IsKnowHowProtected && values.ModifiedDate == PlcBlock.Modified, "batched block values");
            Check(block.BatchReads == 1 && block.PropertyReads == 0, "one batch call and no property read");
            var tag = new PlcTag();
            var tagValues = BatchedAttributeReads.Tag(tag);
            Check(tagValues.Name == "batchedTag" && tagValues.DataTypeName == "Int" && tagValues.LogicalAddress == "%MW0" && !tagValues.IsSafety
                && tagValues.ExternalAccessible && !tagValues.ExternalVisible && tagValues.ExternalWritable, "batched tag values");
            Check(tag.BatchReads == 1 && tag.PropertyReads == 0, "one tag batch call and no property read");
            Check(BatchedAttributeReads.FallbackTypes.Count == 0, "no fallback recorded");
            return Task.CompletedTask;
        }

        internal static Task FailingTypeFallsBackOnceAndOtherTypesKeepBatching()
        {
            BatchedAttributeReads.ResetFallbacks();
            var first = new PlcBlock { Batch = () => throw new EngineeringException("attribute not supported") };
            var values = BatchedAttributeReads.Block(first);
            Check(values.Name == "typed" && values.Number == 3 && values.ProgrammingLanguage == "LAD" && first.PropertyReads == 7, "typed values after a failed batch");
            var second = new PlcBlock();
            BatchedAttributeReads.Block(second);
            Check(second.BatchReads == 0 && second.PropertyReads == 7, "the failed type is not batched again");
            Check(BatchedAttributeReads.FallbackTypes[typeof(PlcBlock).FullName!] == nameof(EngineeringException), "fallback reason recorded");
            var other = new OtherBlock();
            BatchedAttributeReads.Block(other);
            Check(other.BatchReads == 1 && other.PropertyReads == 0, "another native type still batches");
            return Task.CompletedTask;
        }

        internal static Task UnexpectedValuesFallBack()
        {
            foreach (var batch in new Func<IList<object>>[] {
                () => new object[] { "batched", "Ns" },                                           // too few
                () => new object[] { "batched", "Ns", "7", ProgrammingLanguage.SCL, true, false, PlcBlock.Modified }, // wrong type
                () => new object[] { "batched", "Ns", null!, ProgrammingLanguage.SCL, true, false, PlcBlock.Modified }, // null value
                () => null! })
            {
                BatchedAttributeReads.ResetFallbacks();
                var block = new PlcBlock { Batch = batch };
                var values = BatchedAttributeReads.Block(block);
                Check(values.Name == "typed" && values.Number == 3, "typed values after unexpected batch values");
                Check(BatchedAttributeReads.FallbackTypes[typeof(PlcBlock).FullName!] == "UnexpectedValues", "unexpected values recorded");
            }
            BatchedAttributeReads.ResetFallbacks();
            var tag = new PlcTag { Batch = () => new object[] { "batchedTag", "Int", "%MW0", "no", true, false, true } };
            Check(BatchedAttributeReads.Tag(tag).Name == "typedTag" && tag.PropertyReads == 7, "tag falls back on a wrong value type");
            BatchedAttributeReads.ResetFallbacks();
            return Task.CompletedTask;
        }

        private static void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException("Expected " + what); }
    }
}

// Synthetic SDK shapes for the linked BatchedAttributeReads; they do not validate Siemens signatures.
namespace Siemens.Engineering.SW.Blocks
{
    internal enum ProgrammingLanguage { LAD, SCL }
    internal class PlcBlock
    {
        internal static readonly DateTime Modified = new DateTime(2026, 11, 12, 7, 0, 0, DateTimeKind.Utc);
        internal Func<IList<object>> Batch = () => new object[] { "batched", "Ns", 7, ProgrammingLanguage.SCL, true, false, Modified };
        internal int BatchReads, PropertyReads;
        internal IList<object> GetAttributes(IEnumerable<string> names) { BatchReads++; return Batch(); }
        private T Read<T>(T value) { PropertyReads++; return value; }
        internal string Name => Read("typed");
        internal string Namespace => Read("");
        internal int Number => Read(3);
        internal ProgrammingLanguage ProgrammingLanguage => Read(ProgrammingLanguage.LAD);
        internal bool IsConsistent => Read(true);
        internal bool IsKnowHowProtected => Read(false);
        internal DateTime ModifiedDate => Read(Modified);
    }
    internal sealed class OtherBlock : PlcBlock { }
}
namespace Siemens.Engineering.SW.Tags
{
    internal sealed class PlcTag
    {
        internal Func<IList<object>> Batch = () => new object[] { "batchedTag", "Int", "%MW0", false, true, false, true };
        internal int BatchReads, PropertyReads;
        internal IList<object> GetAttributes(IEnumerable<string> names) { BatchReads++; return Batch(); }
        private T Read<T>(T value) { PropertyReads++; return value; }
        internal string Name => Read("typedTag");
        internal string DataTypeName => Read("Int");
        internal string LogicalAddress => Read("%MW0");
        internal bool IsSafety => Read(false);
        internal bool ExternalAccessible => Read(true);
        internal bool ExternalVisible => Read(false);
        internal bool ExternalWritable => Read(true);
    }
}

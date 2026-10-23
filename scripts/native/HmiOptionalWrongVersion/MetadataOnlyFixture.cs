namespace TiaMcp.OptionalStartup
{
    // Unsigned metadata-only fixture. Its identity explicitly is not Siemens.
    // Qualification copies it under candidate module filenames ONLY to exercise
    // AssemblyName.GetAssemblyName version rejection; its code must never load.
    public sealed class MetadataOnlyFixture { }
}

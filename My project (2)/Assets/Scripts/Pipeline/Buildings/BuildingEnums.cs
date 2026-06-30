namespace SFMap.Pipeline.Buildings
{
    // The vocabularies shared by the authored library and the Unity assembler
    // (design #266 data-model.md §3). Kept as a small standalone file so every SO
    // and the importer reference one definition.

    /// <summary>Material *role* an authored submesh carries; Unity resolves the role
    /// to a concrete colour at import via the building's <see cref="NeighborhoodPalette"/>.
    /// iPad/authoring never assigns final colours — only roles (PDF: "Roles, never colors").</summary>
    public enum MaterialRole { Base, Accent1, Accent2, Glass, Metal, Sign }

    /// <summary>Which assembly stage a part belongs to. The assembler (#270) runs the
    /// stages in PDF order (Roof → Door → Garage → Window → BayWindow → Storefront → Sign).</summary>
    public enum PartCategory { Roof, Door, Garage, Window, BayWindow, Storefront, Sign }

    /// <summary>Building-relative facade a placement targets. <c>Street</c> expands to
    /// every ranked <c>street_facades[]</c> entry from the bake sidecar (#268); the
    /// other values are the fixed building faces.</summary>
    public enum Facade { Front, Back, Left, Right, Roof, Street }

    /// <summary>How a placement was authored (data-model.md §Placement Model).</summary>
    public enum PlacementMode { Exact, Procedural, BuildingSpecific }

    /// <summary>How a <see cref="RolePalette"/> turns its colour list into one resolved
    /// colour for a given seed: pick one, lerp across a ramp, or always the first.</summary>
    public enum PaletteMode { Pick, Lerp, Constant }
}

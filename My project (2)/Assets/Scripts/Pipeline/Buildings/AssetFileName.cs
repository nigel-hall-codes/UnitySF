using System.Text;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>
    /// Turns an arbitrary authored string into a legal Unity asset file name (#464).
    ///
    /// <para>The library keys two generated asset families by neighborhood —
    /// <c>Generated/Palettes/&lt;n&gt;.asset</c> and <c>Generated/DistrictWeights/&lt;n&gt;.asset</c> —
    /// and a neighborhood is a <b>fact</b>, not an authored id: the bake copies the geojson
    /// <c>nhood</c> string through <c>classify_building</c> into the buildings sidecar and on into
    /// <c>BuildingFactsJson.neighborhood</c>, and five real SF neighborhoods contain a forward
    /// slash (<c>Sunset/Parkside</c>, <c>Financial District/South Beach</c>,
    /// <c>Castro/Upper Market</c>, <c>Oceanview/Merced/Ingleside</c>, <c>Lone Mountain/USF</c>).
    /// A slash reads as a directory separator, so those names cannot be file names as they
    /// stand.</para>
    ///
    /// <para><b>Only the file name is sanitised.</b> <c>NeighborhoodPalette.neighborhood</c> and
    /// <c>NeighborhoodTemplateWeights.neighborhood</c> keep the true slashed string, because that
    /// is the lookup key: <c>BuildingAssembler</c> loads every palette with <c>LoadAll</c> and
    /// dictionaries them by the field, then <c>ResolvePalette</c> compares it ordinally against the
    /// bake's fact. Nothing ever reads a generated asset back by file name, so the encoding needs
    /// to be legal and unambiguous — it does not need to be pretty or reversible in code.</para>
    ///
    /// <para><b>The scheme is percent-encoding.</b> Each character that cannot appear in a Windows
    /// / Unity file name is replaced by <c>%</c> plus its two upper-case hex digits, and a literal
    /// <c>%</c> is escaped the same way (<c>%25</c>). <c>Sunset/Parkside</c> becomes
    /// <c>Sunset%2FParkside</c>.</para>
    ///
    /// <para><b>Why it cannot collide.</b> <c>%</c> never survives into the output unescaped, so
    /// every <c>%</c> in the result begins an escape and every escape is exactly three characters —
    /// the output is uniquely decodable, and a uniquely decodable encoding is injective. Two
    /// different neighborhoods therefore cannot land on the same file name. Contrast the server's
    /// <c>export.py:_safe()</c>, which replaces every unusual character with <c>_</c>: fine for what
    /// it does (stopping an authored id escaping the export dir) but lossy, so it could not be used
    /// here.</para>
    ///
    /// <para>The injectivity is under <i>ordinal</i> comparison. Windows and macOS file systems are
    /// additionally case-insensitive, so two names differing only in case would still share a file —
    /// that was already true before this change, and no two <c>nhood</c> strings in
    /// <c>python/data/sf_analysis_neighborhoods.geojson</c> differ only in case.
    /// <c>PaletteAssetPathTests</c> asserts both properties.</para>
    ///
    /// <para><b>It is also stable.</b> A name that needs no escaping is returned unchanged, so
    /// <c>Noe Valley</c> and <c>Sunset</c> keep the asset paths they have today and re-importing an
    /// existing library produces no churn.</para>
    /// </summary>
    public static class AssetFileName
    {
        /// <summary>Characters Windows rejects in a file name. The forward slash is the one that
        /// actually occurs in SF neighborhood names; the rest are here so an unexpected string
        /// cannot produce an invalid path either.</summary>
        private const string Illegal = "/\\:*?\"<>|";

        /// <summary>Percent-encodes <paramref name="name"/> into a legal Unity asset file name.
        /// Returns <c>"_"</c> for a null/empty input so a caller that failed to guard still gets a
        /// writable path rather than <c>".asset"</c>.</summary>
        public static string Encode(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";

            var sb = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (NeedsEscape(c, i, name.Length))
                    sb.Append('%').Append(((int)c).ToString("X2"));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True when <paramref name="name"/> can be a file name as it stands — i.e. it is
        /// what <see cref="Encode"/> is trying to produce. A legal name is not necessarily an
        /// <i>unencoded</i> one: <c>Sunset%2FParkside</c> is legal, and is also what
        /// <c>Sunset/Parkside</c> encodes to. Use <c>Encode(x) == x</c> when the question is
        /// "would encoding change this?".</summary>
        public static bool IsLegalFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name[0] == '.') return false;
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ') return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (Illegal.IndexOf(c) >= 0 || c < ' ' || c == (char)0x7F) return false;
            }
            return true;
        }

        // '%' is a legal file-name character but is escaped anyway: it is the escape marker, and
        // escaping it is what makes the output uniquely decodable and therefore collision-free.
        //
        // A trailing '.' or ' ' and a leading '.' are legal mid-name but not at the edges — Windows
        // silently strips a trailing dot or space, and Unity (like Unix) treats a leading dot as a
        // hidden file. That escaping is positional, which keeps the output decodable and so keeps
        // the mapping injective.
        private static bool NeedsEscape(char c, int index, int length)
        {
            if (c == '%' || Illegal.IndexOf(c) >= 0 || c < ' ' || c == (char)0x7F) return true;
            if (index == 0 && c == '.') return true;
            if (index == length - 1 && (c == '.' || c == ' ')) return true;
            return false;
        }
    }
}

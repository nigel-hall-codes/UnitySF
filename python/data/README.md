# Vendored bake-input data

Small reference datasets committed to the repo and consumed by `python/sfmap_bake.py`.
(The large OSM / elevation / parking inputs are *not* vendored — they're gitignored and
live under `My project (2)/Assets/SFMapData/`. These files are small enough to track.)

## `sf_analysis_neighborhoods.geojson`

San Francisco neighborhood boundaries — 41 `MultiPolygon` features in WGS84 lon/lat,
each with a single `nhood` property (the neighborhood name, e.g. `"Mission"`,
`"Twin Peaks"`). This is the same neighborhood vocabulary the parking-regulations CSV's
`analysis_neighborhood` column uses, so building and kerb classifications agree.

| | |
|---|---|
| **Source** | DataSF — "Analysis Neighborhoods" |
| **Dataset page** | https://data.sfgov.org/Geographic-Locations-and-Boundaries/Analysis-Neighborhoods/p5b7-5n3h |
| **Downloaded from** | `https://data.sfgov.org/resource/j2bu-swwd.geojson?$limit=200` (the underlying tabular dataset of the `p5b7-5n3h` map view) |
| **Retrieved** | 2026-06-30 |
| **Name field** | `nhood` |
| **Licence** | **Open Data Commons Public Domain Dedication and License (PDDL)** — public domain, vendoring permitted with no attribution requirement |

### Used by

`sfmap_bake --neighborhoods data/sf_analysis_neighborhoods.geojson`, via
`sfmap/geometry/neighborhood.py`, which projects the polygons into world XZ and offers a
point-in-polygon lookup (building centroid → neighborhood name, `""` if outside all
polygons). Feeds the `neighborhood` field of the building classification sidecar
(design #266, `data-model.md` §1).

### Refreshing

Re-download the underlying dataset (the `p5b7-5n3h` *map view* exports an empty
`FeatureCollection`; fetch its parent tabular dataset `j2bu-swwd` instead):

```bash
curl -sSL --compressed \
  "https://data.sfgov.org/resource/j2bu-swwd.geojson?\$limit=200" \
  -o python/data/sf_analysis_neighborhoods.geojson
```

# San Francisco Building Template Pipeline — Final Design Document (v1)

> Vendored markdown extraction of `~/Downloads/San Francisco Building Template Pipeline.pdf`,
> the source document captured into issue #266. Committed here (#332) so future audits
> (e.g. #297) have a stable, diffable reference instead of a local, uncommitted PDF.
>
> **Editor UX note:** the iPad editor sections below (iPad Editor responsibilities, Building
> Import Workflow, Geometry Authoring, Material System) are **superseded** by
> [#326's UX Design Specification v1.0](https://github.com/nigel-hall-codes/UnitySF/issues/326)
> for the app's creator experience. Treat #326 as source of truth for editor UX; this document
> remains authoritative for the data flow, asset categories, placement system, and server/Unity
> architecture around it.
>
> See also this pipeline's own [`design.md`](design.md) and [`data-model.md`](data-model.md),
> which reconcile this PDF with the existing offline OSM→chunk→Unity generator.

## Objective

Create a complete asset-authoring pipeline that allows architectural pieces to be authored on an iPad and
automatically integrated into a procedural San Francisco city generator in Unity.

The system is designed around:

- OSM building footprints
- Low-poly architecture
- Reusable facade components
- AI-generated signage and storefront graphics
- Neighborhood-aware procedural generation
- Building-specific overrides for important locations

The goal is not to model every building individually.

The goal is to create a growing architectural library that can generate thousands of believable San Francisco
buildings.

## Core Architecture

```
OSM Data
 ↓
Unity Building Importer
 ↓
Building Classification
 ↓
Procedural Generator
 ↓
Generated City
 ↑
 |
Home PC Asset Server
 ↑
 |
 iPad Editor
```

## Major Components

### 1. iPad Editor

Primary asset creation tool.

Responsibilities:

- Browse available buildings
- Load building footprints
- Load existing templates
- View building in 3D
- Import screenshots
- Trace facade elements
- Create meshes
- Assign material roles
- Generate AI signs
- Create placement rules
- Save assets back to home PC

The iPad editor is an authoring tool, not a city generator.

### 2. Home PC Asset Server

Technology: FastAPI · Python · SQLite/Postgres · Local Asset Storage · Optional Blender Processing

Responsibilities:

- Asset storage
- Asset versioning
- Building metadata
- Template management
- AI generation requests
- Unity exports
- Neighborhood definitions
- Material palettes

Acts as the central source of truth.

### 3. Unity Import Pipeline

Responsibilities:

- Import templates
- Import meshes
- Import metadata
- Generate prefabs
- Create ScriptableObjects
- Build procedural buildings
- Generate final city

Unity is responsible for assembly and runtime generation.

## Asset Categories

### Building Parts

Reusable geometry. Examples: Window, Door, Garage Door, Bay Window, Storefront, Awning, Stairs,
Balcony, Fire Escape, Roof Trim, Cornice, Vent, Utility Box, Chimney.

### Sign Assets

Generated or imported graphics. Examples: Store Signs, Window Decals, Billboards, Posters, Murals,
Menus, Street Advertising.

### Building Templates

Defines architectural styles. Examples: Sunset Single Family, Sunset Row House, Mission Mixed Use,
Mission Corner Store, Richmond Apartment, Marina Bay Window House, SOMA Warehouse, Financial
District Midrise.

### Building-Specific Designs

Used for important buildings. Examples: Unique Corner Store, Recognizable Restaurant, Landmark
Building, Special Event Location, Mission Street Hero Building. These are not procedural.

## Asset Organization

```
assets/
  neighborhoods/
    sunset/
    richmond/
    mission/
    marina/
    soma/
    financial_district/
  templates/
    buildings/
    parts/
    signs/
  building_specific/
  exports/
  generated/
```

## Building Import Workflow

### Step 1

Select building from: OSM Building Footprint · Existing Template · Building-Specific Design.

### Step 2

Load into editor. Editor displays: Footprint, Building Mass, Grid, Measurements, Reference Guides.

### Step 3

Import screenshots. Supported: Street View, Phone Photos, Internet References, Architectural
Drawings. Screenshot controls: Move, Scale, Rotate, Crop, Opacity, Lock.

## Geometry Authoring

### Creation Tools

Vertex Tool, Polygon Tool, Rectangle Tool, Line Tool, Extrude Tool, Mirror Tool, Duplicate Tool,
Snap Tool.

### Mesh Output

Saved as GLB. Primary format: glTF / GLB.

### Real World Scale

Everything uses meters. Examples: Door Height 2.1m, Floor Height 3.0m, Garage Width 2.4m–3.0m.

Calibration process: select two points → enter real-world distance → editor scales overlay.

## Material System

The iPad app never chooses final colors. The iPad app assigns material roles.

Supported Roles: Base, Accent 1, Accent 2, Glass, Metal, Sign.

Examples:

- **Base:** main wall surfaces
- **Accent 1:** window trim, door trim, cornices
- **Accent 2:** decorative details, garage accents, storefront accents
- **Glass:** windows, storefront glass
- **Metal:** fire escapes, railings, utility boxes
- **Sign:** storefront graphics, posters, billboards

Unity chooses final colors.

### Neighborhood Palette System

Each neighborhood defines palettes. Example:

- **Sunset:** Warm Beige, Cream, Muted Yellow, Soft Gray
- **Mission:** Colorful, Bold, High Contrast
- **Financial District:** Glass, Steel, Dark Gray, Concrete

Unity randomizes palettes within neighborhood constraints.

## AI Sign Generator

Integrated into iPad editor.

User chooses: Business Type, Neighborhood, Sign Text, Aspect Ratio, Style Preset.

Examples: Coffee Shop, Taqueria, Book Store, Laundry, Hardware Store.

Generated output: PNG, Metadata, Thumbnail. Generated signs become reusable assets.

### AI Providers

Backend abstraction supports: ChatGPT Image Generation, Nano Banana, Future Providers.

The iPad app never talks directly to AI services. Only the home PC server does.

## Placement System

Supports three modes.

### Mode 1 — Exact Layout

Used when a specific facade arrangement is desired. Stores: Precise placement, Precise
dimensions, Precise orientation. Unity reproduces exactly.

### Mode 2 — Procedural Rule

Used when building style should scale. Stores: Placement zones, Probabilities, Repeat behavior,
Constraints.

Example: Bay Window, Front Facade, 35% Chance, Floors 2-3, Center Zone.

Unity adapts to building dimensions.

### Mode 3 — Building-Specific Design

Used when a building should remain unique. Stores: OSM Building ID, Footprint Hash, Exact
Layout, Signs, Materials, Overrides. Unity applies only to that building. Never used
procedurally unless manually converted.

## Placement Metadata

Each placed object stores: Part Type, Facade, Floor, Normalized Position, Scale, Rotation,
Material Roles, Placement Mode, Randomization Rules.

Normalized coordinates: 0.0 = left edge, 0.5 = center, 1.0 = right edge. This allows templates
to adapt to different widths.

## Building Classification

Each building receives: Neighborhood, Building Type, Footprint Shape, Width, Depth, Height,
Floor Count.

Examples: Sunset + Single Family, Mission + Mixed Use, Richmond + Apartment.

Classification determines which templates may be used.

## Template Selection

Example: Neighborhood Mission, Building Type Mixed Use, Width 12m, Floors 3.

Unity searches: Compatible Building Templates, Compatible Windows, Compatible Doors, Compatible
Signs, Compatible Roof Elements. Then assembles a building.

## Asset Metadata

Example:

```json
{
  "id": "sunset_bay_window_01",
  "category": "bay_window",
  "neighborhood_tags": ["sunset", "richmond"],
  "building_type_tags": ["single_family"],
  "material_roles": ["Base", "Accent1", "Glass"],
  "dimensions": {
    "width": 2.2,
    "height": 3.0,
    "depth": 0.6
  }
}
```

## Home PC API

Core endpoints:

- `GET /neighborhoods`
- `GET /building-types`
- `GET /templates`
- `GET /parts`
- `POST /parts`
- `POST /templates`
- `POST /building-specific`
- `POST /ai/signs/generate`
- `POST /export/unity`

## Unity Import Structure

```
Assets/
  SFBuildingTemplates/
    Parts/
    Signs/
    BuildingStyles/
    Materials/
    GeneratedPrefabs/
    BuildingSpecific/
```

## Procedural Building Generation

Input: Footprint, Neighborhood, Building Type, Height, Rules, Templates.

Output: Generated Building Prefab.

Generation order:

1. Create Mass
2. Apply Roof
3. Place Doors
4. Place Garages
5. Place Windows
6. Place Bay Windows
7. Place Storefronts
8. Place Signs
9. Apply Palette
10. Finalize Prefab

## Future Features

### Phase 2

Perspective Correction, Auto UV Generation, Material Painting, Version History, Google Drive
Backup, Asset Review System.

### Phase 3

AI-Assisted Facade Recognition, Screenshot Feature Detection, Automatic Window Detection,
Automatic Storefront Detection, Template Suggestions.

### Phase 4

One-Click Building Style Extraction, Neighborhood Training Sets, Procedural Rule Learning,
Semi-Automatic Template Creation.

## MVP Scope

The first useful version should support: Load OSM Building, Import Screenshot, Trace Window,
Assign Material Roles, Save GLB, Upload To API, Import Into Unity, Place Window On Building.

Once that loop works, expand to: Doors, Garages, Bay Windows, Storefronts, Signs, Roof Elements,
Procedural Rules, Neighborhood Templates.

---

**Philosophy:** create a small number of high-quality reusable architectural pieces and allow
Unity to generate thousands of believable San Francisco buildings from them.

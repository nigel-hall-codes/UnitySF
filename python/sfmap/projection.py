"""lat/lon → world XZ, mirrors GeoProjection.cs."""
import math
from dataclasses import dataclass


@dataclass
class OsmBounds:
    min_lat: float
    max_lat: float
    min_lon: float
    max_lon: float

    @property
    def center_lat(self) -> float:
        return (self.min_lat + self.max_lat) / 2.0

    @property
    def center_lon(self) -> float:
        return (self.min_lon + self.max_lon) / 2.0


@dataclass
class GeoOrigin:
    center_lon: float
    center_lat: float
    meters_per_deg_lon: float
    meters_per_deg_lat: float = 111320.0

    @classmethod
    def from_bounds(cls, bounds: OsmBounds) -> "GeoOrigin":
        center_lat = bounds.center_lat
        center_lon = bounds.center_lon
        meters_per_deg_lon = math.cos(math.radians(center_lat)) * 111320.0
        return cls(
            center_lon=center_lon,
            center_lat=center_lat,
            meters_per_deg_lon=meters_per_deg_lon,
        )


def to_world_xz(lon: float, lat: float, origin: GeoOrigin) -> tuple:
    """Return (x, z) in Unity world space (metres). x=east, z=north."""
    x = (lon - origin.center_lon) * origin.meters_per_deg_lon
    z = (lat - origin.center_lat) * origin.meters_per_deg_lat
    return (float(x), float(z))


def world_rect(bounds: OsmBounds, origin: GeoOrigin) -> tuple:
    """Return (x_min, z_min, width, height) world-space rect for the OSM bounds."""
    sw_x, sw_z = to_world_xz(bounds.min_lon, bounds.min_lat, origin)
    ne_x, ne_z = to_world_xz(bounds.max_lon, bounds.max_lat, origin)
    return (sw_x, sw_z, ne_x - sw_x, ne_z - sw_z)

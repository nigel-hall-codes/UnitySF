# Motorcycle spike (issue #222)

Goal of the spike: prove we can ride a **two-wheeled NWH Vehicle Physics 2 vehicle** that
stays upright at speed and leans into corners, before committing to a full implementation.
NWH has no native motorcycle model, so the risk is the balance — that lives in
[`MotorcycleBalance.cs`](MotorcycleBalance.cs). This doc is how to assemble and ride a test bike.

## What ships in this spike

| File | Role |
|---|---|
| `MotorcycleInputProvider.cs` | Keyboard input via built-in `Horizontal`/`Vertical` axes + Space handbrake. Auto-discovered by NWH — no InputManager setup needed. |
| `MotorcycleBalance.cs` | The "virtual rider": measures roll vs the ground, computes the lean the current turn needs (θ = atan(v·yawRate/g)), and holds it with a PD roll-torque. |
| `SFMap.Vehicles.asmdef` | Lets these scripts reference the NWH assemblies. |

## Assembling a test bike (≈10 min, all in the Unity editor)

NWH's own **Vehicle Setup Wizard** wires the powertrain correctly — far less error-prone than
doing it by hand. We build a primitive bike, let the wizard set it up, then attach our 2 scripts.

1. **Body.** Create an empty GameObject `TestBike` at the origin, scale **(1,1,1)** (the wizard
   rejects non-unit scale). Add a child `Body` — a stretched Cube or Capsule (~0.4 × 1.0 × 2.0,
   long axis along **+Z**). Add a **Box Collider** to `Body` sized to the bike (the wizard needs
   at least one collider to compute mass/inertia/CoM).
2. **Wheels.** Add two child Cylinders (rotate 90° so they roll around X), `WheelFront` and
   `WheelRear`, placed along +Z — e.g. front at `z = +0.7`, rear at `z = -0.7`, both at wheel
   radius height (`y ≈ 0.3`). These are just the visual/measurement meshes; the wizard creates
   the actual `WheelController` objects.
3. **Run the wizard.** Add component **Vehicle Setup Wizard** to `TestBike`. Assign
   `Wheel GameObjects` = `[WheelFront, WheelRear]` (front first). Pick **WheelController3D** as
   the wheel type. Click **Run Setup**. You now have a `VehicleController` with a Rigidbody,
   engine, transmission and two wheels.
4. **Make it a bike, not a narrow car.** In the `VehicleController` inspector:
   - **Powertrain → Wheels / Wheel Groups:** confirm two groups, one wheel each. Front group
     `steerCoefficient = 1`, rear group `steerCoefficient = 0`. Drive (differential output) goes
     to the **rear** wheel; front is free-rolling.
   - **Steering → Maximum Steer Angle:** ~`30`.
   - **Rigidbody:** mass ~`200`. Lower the **Center of Mass** (the wizard sets one from the
     collider; a low CoM helps but the balance script does the real work).
5. **Attach our scripts.** On `TestBike` add **Motorcycle Input Provider** and
   **Motorcycle Balance**. Defaults are a sane starting point.
6. **Scene.** Drop the bike onto a flat ground plane a little above the surface and press Play.

## Riding it

- **W / Up** throttle, **S / Down** brake (and reverse once stopped), **A·D / Left·Right** steer,
  **Space** handbrake.

## Tuning order (do these in sequence)

1. **Stay upright at a stop.** With no input the bike should hold vertical. If it slowly topples,
   raise `Balance P`. If it buzzes/oscillates, raise `Balance D` (or lower P).
2. **Lean the right way.** Ride a steady circle. It should lean *into* the turn. If it leans
   *out* (and wants to fall), flip **`Lean Into Turn Sign`** to `-1`.
3. **Corner feel.** Adjust `Max Lean Angle` (how far it can drop), `Input Lean Angle` (turn-in
   crispness), and `Full Lean Speed` (speed at which full lean is available).
4. **Optional countersteer.** Bump `Counter Steer Assist` off 0 for a more sim feel on turn-in.

## Spike exit criteria

- [ ] Bike holds upright at a standstill and at speed.
- [ ] Leans into corners and tracks a stable circle without falling in or out.
- [ ] Rideable with keyboard; throttle/brake/steer feel coherent.
- [ ] Behaves on a slope (the balance references averaged wheel-contact normals, not world up).

When these hold, we fold the bike into a clean implementation against the streaming world
(spawn onto streamed road like `CarRoadSpawner`, camera follow, gearing/sound polish).

## Known spike limitations (deliberately deferred)

- No camera rig, sound, or gear UI tuning — stock NWH defaults.
- Balance is a "virtual rider" PD model, not full countersteer dynamics (assist is optional).
- Assembly is manual; if the model proves out we can script a prefab builder.

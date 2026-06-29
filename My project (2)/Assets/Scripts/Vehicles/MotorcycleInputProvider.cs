using NWH.VehiclePhysics2.Input;
using UnityEngine;

namespace SFMap.Vehicles
{
    /// <summary>
    /// Feeds keyboard/gamepad input into an NWH <c>VehicleController</c> for the motorcycle spike.
    ///
    /// NWH discovers every <see cref="VehicleInputProviderBase"/> in the scene automatically
    /// (the base <c>InputProvider</c> registers itself in a static <c>Instances</c> list on
    /// enable, and the vehicle's input handler aggregates them while <c>autoSetInput</c> is on),
    /// so this component just needs to exist in the scene — drop it on the bike root.
    ///
    /// Unlike NWH's stock <c>InputManagerVehicleInputProvider</c>, this reads the built-in
    /// "Horizontal"/"Vertical" axes (always present in any Unity project) instead of NWH's
    /// custom "Steering"/"Throttle"/"Brakes" axes, so the spike runs with zero InputManager
    /// setup. Throttle and brake share the vertical axis: push up to accelerate, pull down to
    /// brake (and reverse once stopped). Hold Space for the handbrake / rear-wheel lock.
    /// </summary>
    public class MotorcycleInputProvider : VehicleInputProviderBase
    {
        [Tooltip("Key held to lock the rear wheel (handbrake / stoppie practice).")]
        public KeyCode handbrakeKey = KeyCode.Space;

        public override float Steering()
        {
            return Mathf.Clamp(UnityEngine.Input.GetAxis("Horizontal"), -1f, 1f);
        }

        public override float Throttle()
        {
            return Mathf.Clamp01(UnityEngine.Input.GetAxis("Vertical"));
        }

        public override float Brakes()
        {
            // Pulling the vertical axis negative brakes; NWH swaps brake/throttle to drive
            // reverse once the bike has come to a stop, so this also serves as reverse input.
            return Mathf.Clamp01(-UnityEngine.Input.GetAxis("Vertical"));
        }

        public override float Handbrake()
        {
            return UnityEngine.Input.GetKey(handbrakeKey) ? 1f : 0f;
        }
    }
}

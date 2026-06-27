using NWH.VehiclePhysics2;
using UnityEngine;

namespace SFMap.Vehicles
{
    /// <summary>
    /// Keeps a two-wheeled NWH vehicle upright and leans it into corners.
    ///
    /// NWH Vehicle Physics 2 has no native motorcycle model — it assumes a four-wheel car
    /// whose track width keeps it stable. A two-wheel rig has zero roll support from its
    /// wheels, so something has to supply the balance a rider's body and gyroscopic effects
    /// would. This component is that "virtual rider": each physics step it measures the bike's
    /// roll relative to the ground, decides what lean angle the <i>current</i> turn calls for,
    /// and applies a PD-controlled roll torque about the bike's forward axis to track it.
    ///
    /// Model (spike, sim-leaning):
    ///  - <b>Target lean</b> is the angle that balances the centripetal force of the turn the
    ///    bike is actually making: θ = atan(v·yawRate / g). That is the physically correct lean
    ///    for a steady corner, so the bike leans <i>because</i> it is turning, not the reverse.
    ///    A small input-proportional term is added at speed for crisper turn-in feel.
    ///  - <b>Holding the lean</b> is a PD controller. Because the torque is applied about the
    ///    same forward axis the lean error is measured around, the restoring loop is
    ///    sign-consistent by construction and will not diverge for positive gains — only the
    ///    <i>target</i> needs its sign checked (see <see cref="leanIntoTurnSign"/>).
    ///  - <b>Ground reference</b> is the average of the grounded wheels' contact normals, so the
    ///    bike balances relative to the road surface and stays sane on SF's hills; it falls back
    ///    to world up when airborne, and fades the balance torque out with the grounded fraction
    ///    so the bike doesn't pirouette mid-jump.
    ///
    /// Put this on the bike root alongside the <c>VehicleController</c> and its
    /// <c>Rigidbody</c>. Tune <see cref="balanceP"/>/<see cref="balanceD"/> first (upright and
    /// stable at a stop), then the lean terms (leans into a steady circle without falling in or
    /// out). If the bike leans the wrong way through corners, flip <see cref="leanIntoTurnSign"/>.
    /// </summary>
    [RequireComponent(typeof(VehicleController))]
    [RequireComponent(typeof(Rigidbody))]
    public class MotorcycleBalance : MonoBehaviour
    {
        [Header("Balance (PD roll controller)")]
        [Tooltip("Proportional gain: stiffness pulling the bike toward its target lean. " +
                 "Too low and it topples; too high and it twitches. Tune at a standstill first.")]
        public float balanceP = 4000f;

        [Tooltip("Derivative gain: damps the roll rate so the bike settles instead of oscillating.")]
        public float balanceD = 800f;

        [Tooltip("Overall multiplier on the balance torque, applied as acceleration (mass-independent).")]
        public float torqueScale = 1f;

        [Header("Lean target")]
        [Tooltip("Maximum lean angle (degrees) the bike is allowed to reach in either direction.")]
        public float maxLeanAngle = 45f;

        [Tooltip("Extra lean (degrees) added straight from steering input at full speed, for " +
                 "sharper turn-in than the centripetal term alone gives. Set 0 for pure physics lean.")]
        public float inputLeanAngle = 12f;

        [Tooltip("Forward speed (m/s) at which speed-scaled lean terms reach full strength. " +
                 "Below this, lean fades out and the controller mostly just holds the bike upright.")]
        public float fullLeanSpeed = 8f;

        [Tooltip("Flip if the bike leans OUT of corners (toward falling over) instead of into them.")]
        public float leanIntoTurnSign = 1f;

        [Header("Steering assist")]
        [Tooltip("Adds a touch of countersteer: a brief steer impulse opposite the lean change " +
                 "when initiating a turn, then steers into it. 0 disables (NWH steering only).")]
        [Range(0f, 1f)]
        public float counterSteerAssist = 0f;

        [Tooltip("Degrees of extra front-wheel steer per unit of lean error, fed to NWH's steering. " +
                 "Used by the countersteer assist; kept small so it nudges rather than fights.")]
        public float steerAssistGain = 0.15f;

        private const float Gravity = 9.81f;

        private VehicleController _vc;
        private Rigidbody _rb;

        private void Awake()
        {
            _vc = GetComponent<VehicleController>();
            _rb = GetComponent<Rigidbody>();
        }

        private void FixedUpdate()
        {
            if (_vc == null || _rb == null || !_vc.IsInitialized)
            {
                return;
            }

            Vector3 forward = transform.forward;

            // --- Ground reference: average contact normal of grounded wheels, else world up ---
            Vector3 groundUp = Vector3.up;
            int grounded = 0;
            int wheelCount = _vc.powertrain.wheels.Count;
            Vector3 normalSum = Vector3.zero;
            for (int i = 0; i < wheelCount; i++)
            {
                var w = _vc.powertrain.wheels[i].wheelUAPI;
                if (w != null && w.IsGrounded)
                {
                    normalSum += w.HitNormal;
                    grounded++;
                }
            }
            if (grounded > 0)
            {
                groundUp = normalSum.normalized;
            }
            float groundedFraction = wheelCount > 0 ? (float)grounded / wheelCount : 0f;

            // --- Current lean: signed roll of the bike's up vs ground up, about forward axis ---
            // Project both onto the plane perpendicular to forward so the measurement is pure roll.
            Vector3 refUp = Vector3.ProjectOnPlane(groundUp, forward).normalized;
            Vector3 bikeUp = Vector3.ProjectOnPlane(transform.up, forward).normalized;
            float currentLean = Vector3.SignedAngle(refUp, bikeUp, forward);

            // --- Roll rate (about forward) and yaw rate (about ground up) ---
            float rollRate = Vector3.Dot(_rb.angularVelocity, forward) * Mathf.Rad2Deg;
            float yawRate = Vector3.Dot(_rb.angularVelocity, groundUp); // rad/s
            float forwardSpeed = Vector3.Dot(_rb.linearVelocity, forward);
            float speedFactor = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(0.01f, fullLeanSpeed));

            // --- Target lean: angle that balances the centripetal force of the actual turn ---
            float centripetalLean = Mathf.Atan2(forwardSpeed * yawRate, Gravity) * Mathf.Rad2Deg;
            float steerInput = _vc.input.Steering;
            float inputLean = steerInput * inputLeanAngle * speedFactor;
            float targetLean = leanIntoTurnSign * (centripetalLean + inputLean);
            targetLean = Mathf.Clamp(targetLean, -maxLeanAngle, maxLeanAngle);

            // --- PD roll torque about forward axis. Sign-consistent: positive error and positive
            //     torque both increase the about-forward angle, so the loop is self-correcting. ---
            float error = targetLean - currentLean;
            float torque = (balanceP * error - balanceD * rollRate) * torqueScale * groundedFraction;
            _rb.AddTorque(forward * (torque * Mathf.Deg2Rad), ForceMode.Acceleration);

            // --- Optional countersteer assist: nudge NWH's front wheel to help initiate the lean. ---
            if (counterSteerAssist > 0f)
            {
                _vc.steering.externallyAddedAngle =
                    Mathf.Clamp(error * steerAssistGain * counterSteerAssist, -10f, 10f);
            }
            else
            {
                _vc.steering.externallyAddedAngle = 0f;
            }
        }
    }
}

using UnityEngine;

namespace Rossa88
{
    // The car body's inertia, fixed instead of Unity's guess from its colliders ("inertia" in a Car07 config). Unity derives
    // it from every collider as if each were solid; the car's lids, doors and bumpers became grids of thin boxes (so a
    // held part can come close enough to attach), which moved that guess and made the car pitch and jump on bumps. The value
    // is per kilogram, so parts added or removed (the game changes the body's mass) keep it in proportion.
    public class InertiaPin : MonoBehaviour
    {
        public Vector3 perKg;
        public Vector3 rotationEuler;
        private Rigidbody rb;
        private float appliedMass = -1;

        private void Start() { rb = GetComponent<Rigidbody>(); Apply(); }

        private void FixedUpdate()
        {
            if (rb && !Mathf.Approximately(rb.mass, appliedMass)) Apply();
        }

        private void Apply()
        {
            if (!rb || perKg == Vector3.zero) return;
            rb.inertiaTensor = perKg * rb.mass;
            rb.inertiaTensorRotation = Quaternion.Euler(rotationEuler);
            appliedMass = rb.mass;
        }
    }
}

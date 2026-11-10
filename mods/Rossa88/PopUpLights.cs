using UnityEngine;

namespace Rossa88
{
    // Pop-up headlights for a CarBuild car: while the car's light switch is on, each pod (the headlight slot with its lamp
    // item, and the painted cover) swings up about its rear edge; switched off, it folds back down. Built into the car prefab
    // by CarBuild ("popups" in the car's config), so spawned and loaded cars have it.
    internal sealed class PopUpLights : MonoBehaviour
    {
        public carscript car;
        public Transform[] pods;
        public Quaternion[] down;
        public float angle = -55f;
        public float seconds = .6f;
        private float t;

        private void Update()
        {
            if (!car || pods == null) return;
            var use = car.lightUsable;
            bool on = use && use.currentTurnState > 0;
            float was = t;
            t = Mathf.MoveTowards(t, on ? 1 : 0, Time.deltaTime / Mathf.Max(.05f, seconds));
            if (Mathf.Approximately(was, t) && Time.frameCount % 30 != 0) return;
            float eased = t * t * (3 - 2 * t);
            for (int i = 0; i < pods.Length; i++)
                if (pods[i]) pods[i].localRotation = down[i] * Quaternion.AngleAxis(angle * eased, Vector3.right);
        }
    }
}

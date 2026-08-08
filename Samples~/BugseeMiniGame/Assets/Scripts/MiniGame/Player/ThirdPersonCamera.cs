using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Third-person follow that orbits the character's ground pivot (feet),
    /// so yaw keeps character + camera rotating around the same origin.
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField] Vector3 offset = new Vector3(0f, 3.2f, -5.5f);
        [SerializeField] float followLerp = 14f;
        [SerializeField] float lookHeight = 0.9f;
        [SerializeField] float collisionRadius = 0.25f;

        Transform _target;

        public void SetTarget(Transform target)
        {
            _target = target;
            if (_target != null)
                SnapNow();
        }

        public void SnapNow()
        {
            if (_target == null) return;
            transform.position = DesiredPosition();
            transform.rotation = DesiredRotation();
        }

        void LateUpdate()
        {
            if (_target == null) return;

            // Pivot under the character center (ground / feet), not under the camera.
            var pivot = CharacterPivot();
            var desired = DesiredPosition();
            desired = TrimForCollision(pivot, desired);

            float t = 1f - Mathf.Exp(-followLerp * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, DesiredRotation(), t);
        }

        Vector3 CharacterPivot()
        {
            // Feet / ground point under the character — shared yaw origin with the anteater.
            return _target.position;
        }

        Vector3 DesiredPosition()
        {
            // Orbit behind the character in its local yaw space around the feet pivot.
            return _target.TransformPoint(offset);
        }

        Quaternion DesiredRotation()
        {
            var lookAt = _target.position + Vector3.up * lookHeight;
            return Quaternion.LookRotation(lookAt - transform.position, Vector3.up);
        }

        Vector3 TrimForCollision(Vector3 pivot, Vector3 desired)
        {
            var lookPivot = pivot + Vector3.up * lookHeight;
            var dir = desired - lookPivot;
            float dist = dir.magnitude;
            if (dist < 0.01f) return desired;
            if (Physics.SphereCast(lookPivot, collisionRadius, dir.normalized, out var hit, dist,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                return lookPivot + dir.normalized * Mathf.Max(0.6f, hit.distance - 0.15f);
            }

            return desired;
        }
    }
}

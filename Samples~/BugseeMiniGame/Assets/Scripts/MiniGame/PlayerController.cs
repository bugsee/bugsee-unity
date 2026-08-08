using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>Legacy capsule controller — labyrinth uses <see cref="AnteaterController"/>.</summary>
    public sealed class PlayerController : MonoBehaviour
    {
        void Awake()
        {
            Debug.LogWarning("[MiniGame] PlayerController is obsolete; use AnteaterController.");
            enabled = false;
        }
    }
}

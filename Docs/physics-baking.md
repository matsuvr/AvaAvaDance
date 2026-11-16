# Physics baking

The physics bake command processes the current model's dynamic bones and their
tracked child endpoints into native base keys at 30 fps. It uses the selected
Bullet/PhysBone backend's existing 60 Hz integration, warming up sequentially from
frame 0. It does not import/export an MMD motion format.

Each generated key has physics disabled so replay does not apply the simulation
again to the baked result. Both position and rotation are captured relative to
the track's bind pose. Child endpoint positions are included to retain spring
stretch. Other models are evaluated as dependencies but are not baked.

The requested inclusive range overwrites existing base keys. Missing sampled
keys immediately before/after that range are added to restore the prior
physics flags. Existing outside keys are not rewritten. These boundary additions
can change interpolation in neighbouring segments; this is not an exact
subframe-preserving curve split. Bake the entire motion when exact untouched
boundary interpolation is important. All inserted/replaced keys share one Undo.

Layer-composed target bones and target bones overlapping an IK chain are rejected
rather than silently applying their animation twice. The maximum plan is one
million keys including boundary reserves. Existing scene physics settings are
restored afterwards; changing the physics mode is not part of the edit.

Cancellation (button or Escape) and solver errors discard every planned key.
No keys are changed until the whole plan succeeds. The original frame, draft
values, transforms, render clock and physics mode are restored. Model identity
and track revisions are checked throughout and immediately before commit.

## Verification

`Tests/PhysicsBakeHarness` compiles the production range/guard planner and checks
bounds, zero/max-frame edges, large-count overflow and all 5,151 inclusive ranges
within 0..100. This is **not a simulation or Unity runtime test**.

Unity 2022.3.22f1 verification still required:

1. With an imported VRM/VRChat avatar containing spring/PhysBone links, run once
   with PhysBone and once with Bullet. Bake frames 0..90; inspect head motion,
   hair/skirt and stretched endpoints against a recording of the original.
2. Repeat from reset state; compare generated keys within floating point
   tolerances. Do not assert bitwise backend determinism across platforms.
3. Play baked range with physics enabled, then disabled: baked joints must not
   receive a second simulation. Check both drawing and movie export.
4. Undo once restores all keys and provenance; redo once reapplies the result.
   Save/reopen the native project and repeat playback.
5. Test partial range with/without existing boundary keys; verify no existing
   outside keys changed and physics resumes at end+1. Inspect boundary blending.
6. Cancel during warm-up and during sampling, including Escape. Compare keys,
   drafts, current frame and transforms; no partial results should remain.
7. Reject reversed/out-of-range limits, more than one million generated keys,
   layered target bones and target IK overlap with an actionable explanation.
8. Force a native solver error; verify mode/UI restoration and no key changes.

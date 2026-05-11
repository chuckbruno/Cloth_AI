# Cloth_AI - Soccer Net Collision Simulation

## Project Overview
Unity project implementing realistic soccer ball vs goal net collision simulation using a from-scratch XPBD cloth solver. The end goal is an Obi-Cloth-quality "ball catches in the pocket and is gently pushed back out" interaction.

## Project Layout (current)
- Unity 2022.x, URP (`com.unity.render-pipelines.universal` 14.0.12)
- Test scene: `Assets/Scenes/ClothSim.unity`
- Goal net mesh: `Assets/Models/SM_GoalNet.fbx`
- Goal net material: `Assets/Models/M_Net.mat`
- All new code lives under `Assets/Scripts/GoalNetXPBD/` (isolated assembly `GoalNetXPBD.Runtime`, Editor assembly `GoalNetXPBD.Editor`).
- This project was started from a clean slate — there are no legacy cloth/net scripts to coexist with. Anything previously written elsewhere can be ignored.
- Ball prefab is **user-supplied** (a Rigidbody + SphereCollider GameObject). It is dragged into `BallPool.ballPrefab` from the Inspector. There are no pre-existing ball assets in the repo.
- No PhysicMaterial assets exist yet. Add them later if specific friction/bounciness tuning is needed.

## Technical Decisions

### Simulation Approach
- **From-scratch XPBD** (Extended Position-Based Dynamics). High physical realism is the priority — correct deformation propagation, energy absorption, soft pocket, elastic recovery.
- **Ball physics**: Ball uses Unity's built-in Rigidbody. The net applies reaction forces back to the Rigidbody each FixedUpdate.

### Mesh & Topology
- Net mesh: regular triangle mesh, ~2300 vertices.
- Fixed points are identified by **vertex color black** (consistent with the convention assumed for `SM_GoalNet.fbx`).
- Performance target: PC only, effect-priority over performance. 60 fps is *not* a hard requirement; heavy substepping and high iteration counts are acceptable.

### Goal Net Topology
- Full 4-sided net: top panel + left side + right side + back panel, modeled as one continuous mesh.
- Fixed (black) vertices are expected along the goal-frame-facing edges: front-top crossbar, two front posts, ground line at the post bases. Back / inner vertices are free.
- Ball can enter the front opening and impact any of the 4 inner panels — collision must be position-projection based (not one-sided normal checks) so that hits work from either side of each panel.
- Bending constraints across panel-seam edges are essential for realistic corner deformation.

### Goal Frame (Out of Scope)
- The rigid frame (posts + crossbar) is **not** simulated by us. It is a separate object with standard Unity colliders. Ball-vs-frame is handled by Unity Rigidbody physics directly.

### Ball-Net Interaction Behavior (Target)
Mimics a real soccer net catching a ball:
1. Ball impacts net and pushes vertices outward, forming a "pocket" that conforms to the ball.
2. Ball continuously decelerates as it presses into the net (elastic energy stored in net).
3. Ball's velocity reaches zero while still embedded in the pocket.
4. Net gradually pushes the ball back out (elastic recovery).

Implications:
- Net must be **soft enough** that the ball can compress a visible pocket — not a rigid wall.
- Net must provide a **distance-based restoring force** proportional to stretch.
- Net constraints must **transmit force back to the ball** via `Rigidbody.AddForce` each FixedUpdate.
- Damping must be tuned so the ball decelerates to 0 instead of bouncing off elastically.
- Net stiffness recovers to rest shape after the ball stops, smoothly returning the ball.

### Single-Ball Assumption
- Only one ball touches the net at a time → use a dedicated, optimized sphere-vs-net collision path (no broadphase, no collision-pair list).
- The launch system supports multiple balls in flight (object pool), but the cloth solver assumes only one is interacting with the net at a moment.

## Architecture

### Core Algorithm: XPBD
- Verlet-style position integration: `x_new = x + (x - x_prev) + a * dt²`.
- **Substeps**: 4–8 per FixedUpdate for stability under high-speed ball impact.
- **Constraint iterations**: 10–20 per substep.
- XPBD compliance parameter α gives time-step independent stiffness — critical for the soft-pocket behavior.

### Constraint Types
1. **Structural distance constraints** along every mesh edge (controls stretch resistance; tuned to allow some stretch so the pocket can form).
2. **Bending constraints** between triangle pairs sharing an edge (dihedral form). Prevents the net from folding flat.
3. **Aerodynamic constraints** (Obi-style, light-weight per-triangle drag) — gives the net its gentle floppy look at rest.
4. **Pin constraints** for vertex-color-black vertices (infinite mass).
5. **Collision constraints** — projects vertices inside the ball sphere back to the surface, **inside the constraint solve loop** (not as a post-step). This is what avoids the jitter typical of post-step resolution.

### Ball-Net Coupling (Bidirectional)
- **Net → Ball**: After solving constraints each FixedUpdate, aggregate the position correction applied to vertices by the ball-collision constraints, convert to impulse, and call `Rigidbody.AddForce(-impulse, ForceMode.Impulse)` on the ball. This is the reaction that decelerates and eventually reverses the ball.
- **Ball → Net**: At the start of each substep, project vertices inside the ball sphere back to the sphere surface (with a small skin offset to prevent visual intersection).
- **Velocity transfer**: ball-vertex relative velocity is partially transferred via the collision constraint with restitution 0 (pure position projection). Damping comes from PBD position-based damping + global drag, **not** from velocity reflection.

### Stability Techniques
- Substep `Time.fixedDeltaTime` into N substeps.
- Clamp maximum free-vertex velocity per substep.
- Apply global drag after each substep (`v *= (1 - drag * dt)`).
- XPBD compliance parameterization keeps stiffness independent of dt.

### Reference: Obi Cloth
Obi Cloth (http://obi.virtualmethodstudio.com/) is the quality benchmark. Techniques we adopt: XPBD, particle-based simulation, substepping, layered constraints (distance + bending + aerodynamic + pin), collision-as-constraint inside the solve loop, iterating collision *with* other constraints.
Obi features we deliberately skip: tearing, self-collision, SDF colliders, multi-collider broadphase, Burst/Jobs/ECS performance work — none are needed at our scale (PC, 2300 verts, single ball).

## File Structure

### Implemented
```
Assets/Scripts/GoalNetXPBD/
├─ BallLauncher.cs                        ← Ball launcher (called by UI button or Space key)
├─ BallPool.cs                            ← Fixed-size object pool, 5s auto-return
├─ GoalNetMesh.cs                         ← Mesh -> particles/edges/pin flags (no MonoBehaviour)
├─ GoalNetSimulator.cs                    ← Phase-1 XPBD MonoBehaviour: gravity + pin + distance constraint
├─ GoalNetXPBD.asmdef                     ← Runtime assembly
└─ Editor/
   ├─ BallLauncherUIInstaller.cs          ← Menu Tools/Cloth_AI/Setup Launch Button In Scene
   ├─ GoalNetVertexColorInspector.cs      ← Menu Tools/Cloth_AI/Inspect Goal Net Vertex Colors (vertex-color visualizer)
   └─ GoalNetXPBD.Editor.asmdef           ← Editor-only assembly
```

### Planned (not yet implemented)
```
Assets/Scripts/GoalNetXPBD/
├─ GoalNetConstraints.cs      ← Bending + aerodynamic constraint solvers (phase 2 / 5)
├─ GoalNetBallCoupling.cs     ← Sphere-vs-net collision + reaction force (phase 3 / 4)
└─ (Optional) GoalNetDebug.cs ← Gizmos and runtime tuning helpers
```

## Ball Launch System (implemented 2026-05-11)

A simple test harness for shooting balls at the net. Independent of the (still-pending) cloth solver.

### Components
- **`BallLauncher`** (MonoBehaviour, runtime): public `LaunchBall()` is wired to a uGUI Button OnClick. Uses two scene Transforms — `spawnPoint` (origin) and `aimPoint` (direction target) — plus a configurable `launchSpeed` and optional `initialAngularVelocity`. Also supports keyboard launch (Space) and a reset key (R).
- **`BallPool`** (MonoBehaviour, runtime): fixed-size pool. Default size 8, default auto-return 5 s. Pre-instantiates inactive balls on Awake. `Get(pos, rot, vel)` activates and returns a ball; a coroutine returns it to the pool 5 s later. If the pool is exhausted, the oldest active ball is forcibly recycled. `ReturnAll()` recalls every active ball at once (R-key reset).
- **`BallLauncherUIInstaller`** (Editor): menu `Tools/Cloth_AI/Setup Launch Button In Scene`. Idempotently creates an EventSystem (if missing), a Screen-Space-Overlay Canvas named `GoalNetXPBD_UI`, and a `LaunchBallButton` anchored to the bottom-right. Finds the first `BallLauncher` in the scene and **persistently** binds the button's OnClick to `BallLauncher.LaunchBall()` via `UnityEventTools.AddPersistentListener` (so the binding survives scene save/reload).

### Ball Prefab Requirements (user-supplied)
- Root with `Rigidbody` (Use Gravity = true; recommended Mass = 0.45 kg).
- Root with `SphereCollider` (recommended Radius = 0.11 m).
- A visible mesh (any sphere model is fine).
- Once authored, drag it into `BallPool.ballPrefab` in the Inspector.

### Wiring Steps (summary; full version in README.md)
1. In ClothSim.unity, create a GameObject `BallLauncher`. Add `BallPool` and `BallLauncher` components.
2. Drag a Ball prefab into `BallPool.ballPrefab`.
3. Create two empty Transforms `SpawnPoint` and `AimPoint`, place them in front of the goal, and assign them to the launcher.
4. Run menu `Tools/Cloth_AI/Setup Launch Button In Scene`. Save the scene.
5. Press Play; click "Launch Ball" or press Space.

## Open Questions Still To Confirm
- [ ] Additional reference videos/images/effects from user.
- [x] Visual rendering: keep MeshRenderer with dynamic `mesh.vertices` updates (decided 2026-05-11 — runtime Mesh copy + RecalculateNormals each FixedUpdate).
- [ ] Whether to add a default PhysicMaterial for the ground (currently none in the project).

## Development Log
- 2026-04-30: Initial technical discussion. Decided on from-scratch PBD approach, high realism target, Rigidbody-driven ball.
- 2026-04-30: Confirmed mesh ~2300 verts regular triangles, vertex-color-black for fixed points, "pocket-and-return" ball behavior, single ball, PC-only effect-priority. Chose XPBD + substepping architecture. Drafted file structure.
- 2026-04-30: Confirmed 4-sided full goal net topology (top + both sides + back). Goal frame is out of scope — handled by standard Unity colliders.
- 2026-04-30: User referenced Obi Cloth as quality benchmark. Confirmed XPBD direction. All new code will live in isolated folder `Assets/Scripts/GoalNetXPBD/`. Will add aerodynamic constraints (Obi-style). Collision handled inside constraint-solve loop per Obi pattern, not as a post-step.
- 2026-05-11: Cleaned up AGENTS.md to match the current project state — removed references to non-existent legacy scripts (NetSimulation/SoccerNet/PBDCloth/NetImpact/ClothSmulator) and non-existent assets (Art/Models/*, PMat_*.physicMaterial, SampleScene). Updated paths to the actual `Assets/Models/SM_GoalNet.fbx` + `Assets/Scenes/ClothSim.unity`.
- 2026-05-11: Implemented ball launch test harness — `BallLauncher.cs`, `BallPool.cs`, and `BallLauncherUIInstaller.cs` (Editor menu for one-click UI install). Pool size 8, 5 s auto-return. Authored `README.md` (Chinese) covering scene setup, prefab requirements, parameter tuning, and a net-effect acceptance checklist for use once the XPBD solver is implemented.
- 2026-05-11: User confirmed ball launcher works in Play mode. Started Phase 0 of the XPBD solver: vertex-color debugger. Added `GoalNetVertexColorInspector.cs` — Editor window + Scene gizmos that visualize black/white/other vertices on the goal-net mesh, with adjustable thresholds (default black RGB <= 0.1, white RGB >= 0.9). Window also has Print Histogram and Frame Pinned buttons. Decision: if mesh has no vertex colors at all, the inspector reports an error and the future XPBD solver will refuse to run — artist must paint pin colors. Phases planned: 1) Mesh + Pin + Distance + gravity; 2) Bending; 3) Sphere collision projection; 4) Reaction force back to Rigidbody; 5) Tuning + aerodynamic constraints. User testing happens after each phase.
- 2026-05-11: User confirmed vertex colors on SM_GoalNet are correctly authored (red Gizmos = pinned along goal-frame edges, green = free interior). Implemented Phase 1 of the XPBD solver. New files: `GoalNetMesh.cs` (data struct: rest positions, isPinned[], invMass[], deduped Edge[] with restLength) and `GoalNetSimulator.cs` (MonoBehaviour). Solver runs in world space, instantiates a runtime Mesh copy in Start (FBX never mutated), and per FixedUpdate does N substeps of: predict (gravity, pinned snap to transform.TransformPoint(rest)), `iterations` × per-edge XPBD distance solve with α̃ = compliance/dt², velocity recovery, damping, speed clamp. Writes back via mesh.vertices + RecalculateNormals/Bounds. Defaults: substeps=4, iterations=12, distanceCompliance=0 (rigid; user can dial up), damping=0.02, maxVelocity=50. Includes `[ContextMenu("Reset To Rest Pose")]`. Closed open question: rendering stays as dynamic-mesh-vertex updates (no skinned mesh).
- 2026-05-11: User reported that ropes detach from the net in Play mode. Root cause: the FBX contains independent sub-meshes (rope + net) whose touching endpoints share world position but have different vertex indices, so distance constraints never connect them. Fix: added vertex welding to `GoalNetMesh`. New flow: build a particle list by spatial-hash merging of mesh vertices within `weldDistance` (mesh-local units, default 0.001 = 1mm); store `vertexToParticle[]` for mesh write-back; pin propagation = OR (any black mesh vertex pins the merged particle); edges are built in particle space with self-edge filtering. `GoalNetSimulator` now operates on particles; mesh write-back fans particles back out to all mesh vertices via the redirection table. Inspector exposes `weldDistance` (set 0 to disable). Console reports `meshVerts=X, particles=Y (welded N away)` so the user can verify N>0.

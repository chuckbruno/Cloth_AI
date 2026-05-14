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
- 2026-05-11: Recovered missing `GoalNetMesh.cs` implementation after discovering it had been reduced to a stub while `GoalNetSimulator` still depended on `Initialize`, `restPositionsLocal`, `isPinned`, `invMass`, `vertexToParticle`, and `edges`. The replacement builds welded particles, propagates black vertex pinning, and generates deduplicated structural edges from all mesh submeshes. `dotnet build GoalNetXPBD.Runtime.csproj` passes.
- 2026-05-11: Phase 1 performance pass before Phase 2. User reported the hanging net no longer falls apart but Play Mode ran at ~2 FPS. Main bottlenecks were high default solver work (`substeps=4`, `iterations=12`) plus per-write `Mesh.RecalculateNormals()` / `RecalculateBounds()`. Updated `GoalNetSimulator` realtime defaults to `substeps=2`, `iterations=6`, disabled normal/bounds recalculation by default, added `recalculateNormals`, `recalculateBounds`, `geometryRecalculateInterval`, cached pinned world rest positions, and changed mesh write-back to use one cached `worldToLocalMatrix.MultiplyPoint3x4` per vertex instead of `Transform.InverseTransformPoint`. Added context menus `Apply Realtime Preview Settings` and `Apply High Quality Settings`. User confirmed FPS improved to ~200. Keep this performance budget in mind when adding Phase 2 bending; avoid re-enabling per-frame normal/bounds recalculation unless needed for visual inspection.
- 2026-05-11: Implemented Phase 2 bending constraints. Chose a lightweight adjacent-triangle bending model rather than full dihedral angles for this first pass: `GoalNetMesh` now records shared triangle edges in particle space and creates a `BendingConstraint` between the two opposite particles, using their rest distance as the target. Degenerate welded triangles are skipped; structural edges and bending pairs are deduplicated. `GoalNetSimulator` adds `enableBending`, `bendingCompliance` (default 0.0005), a separate `_bendingLambda[]`, and solves bending constraints after distance constraints inside each XPBD iteration. Realtime preset keeps bending enabled at 0.0005; high-quality preset uses 0.0001. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Next Unity validation: check Console `bending=N`, compare FPS with bending on/off, and tune `bendingCompliance` so the net resists folding without becoming a rigid sheet.
- 2026-05-11: User validated Phase 2 in Unity. With bending enabled, the net shape looks acceptable and FPS holds around 70. Treat this as the Phase 2 baseline before collision work. Phase 3 should start with one-way sphere projection only (ball pushes net particles out of the sphere) and defer net-to-ball reaction force to Phase 4, so any new behavior/performance cost is easy to isolate.
- 2026-05-11: Implemented Phase 3 one-way sphere projection. `BallPool` now exposes `CurrentActiveBall`, returning the most recently launched active pooled ball (or any active pooled ball fallback). `GoalNetSimulator` adds `enableBallCollision`, optional `collisionBall`, optional `ballPool` auto-discovery, `collisionSkin`, and `collisionSearchMargin`. Each XPBD iteration now runs `SolveBallCollision()` after distance and bending: free predicted particles inside the active ball's `SphereCollider` are projected to the sphere surface plus skin. This is intentionally one-way; no `Rigidbody.AddForce` reaction is applied yet. Phase 4 will aggregate collision corrections and push impulses back to the ball. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Unity validation: launch one ball, verify the net visibly avoids/forms around the sphere, confirm the ball still passes through/continues physically because reaction force is not implemented, and compare FPS against the Phase 2 ~70 baseline.
- 2026-05-11: User validated Phase 3 qualitatively: the ball pushes the net open and the net shakes lightly, but contact causes noticeable FPS drops. Implemented Phase 4 first-pass reaction force with bounded impulses. `GoalNetSimulator` now tracks the active collision Rigidbody, accumulates the opposite of particle collision corrections as `_pendingBallImpulse`, scales by `reactionImpulseScale` (default 0.35), and clamps to `maxReactionImpulse` (default 2 N*s) before calling `Rigidbody.AddForce(..., ForceMode.Impulse)` once per FixedUpdate. Added tuning fields `enableBallReaction`, `collisionParticleMass`, `reactionImpulseScale`, and `maxReactionImpulse`; realtime/high-quality presets set conservative defaults. This is a first-pass coupling, not final tuning. If contact FPS remains poor, reduce `iterations` from 6 to 4 or lower `collisionSearchMargin` before adding more collision features. `dotnet build GoalNetXPBD.Runtime.csproj` passes.
- 2026-05-11: User reported ball still does not decelerate regardless of reaction tuning and asked whether `Collision Ball = None` is the problem. It should not be: if the net is being pushed, the simulator has found the active ball through `BallPool.CurrentActiveBall`. More likely root cause was symmetric sphere-surface particle corrections cancelling in `_pendingBallImpulse`. Added `_pendingVelocityOpposingImpulse` and `velocityOpposingImpulseScale`; each particle correction now also contributes scalar impulse magnitude, applied opposite the current ball velocity during `ApplyBallReactionImpulse()`. Realtime preset uses 1.0; high-quality uses 1.25. README now clarifies when `Collision Ball` can remain empty and suggests raising velocity-opposing scale/max impulse if deceleration is still weak.
- 2026-05-11: User found effective Phase 4 tuning requires `collisionParticleMass` around 0.5 or higher and `maxReactionImpulse` around 20. This is physically plausible for the current checked-in `Assets/Models/SoccerBall.prefab`: Rigidbody mass is 1 kg and effective sphere radius is about 0.25 m (`SphereCollider.radius=0.5`, object scale=0.5), not the earlier README recommendation of 0.45 kg / 0.11 m. At `launchSpeed=25 m/s`, stopping a 1 kg ball requires roughly 25 N*s impulse, so a 20 N*s cap is reasonable. Updated defaults/presets accordingly and clarified that `collisionParticleMass` is an effective impulse conversion coefficient, not literal per-vertex mass.
- 2026-05-11: User tried changing the ball toward realistic radius/mass. With `SphereCollider.radius=0.11` the net stopped responding; with radius 1 the net moved again; with mass 0.45 the net deformation became small. Root causes: Unity effective sphere radius is `SphereCollider.radius * max(lossyScale)`, so the current prefab scale 0.5 makes radius 0.11 behave as 0.055 m, and the current collision path is vertex/particle-vs-sphere rather than swept triangle/edge collision, so small balls can miss particles. Added `collisionRadiusPadding` (default 0.08 realtime, 0.06 high quality) to inflate only the solver collision radius while keeping visual ball size realistic. Longer-term improvement should be swept sphere-vs-edge/triangle collision or a rope-thickness collision model.
- 2026-05-11: User provided a better tuned default set from Unity Inspector and asked to make it the default: `distanceCompliance=1e-6`, `bendingCompliance=0.01`, `collisionRadiusPadding=0.11`, `collisionParticleMass=0.43`, `reactionImpulseScale=0.5`, `maxReactionImpulse=6.5`, with existing realtime settings (`substeps=2`, `iterations=6`, `collisionSkin=0.01`, `collisionSearchMargin=0.05`, `velocityOpposingImpulseScale=1`, damping 0.02, normal/bounds recalculation off). Updated field defaults and `Apply Realtime Preview Settings`. User also observed deformation is too local: only a small net region moves, lacking real soccer-net large-area coupling. This is expected from the current vertex-only sphere projection plus local distance/bending propagation. Near-term tuning: raise `distanceCompliance` to 1e-5/1e-4 and/or iterations to 8-12. Better feature work: swept sphere-vs-edge/triangle collision, collision influence spreading to neighboring particles, and aerodynamic/global damping tuning.
- 2026-05-12: Implemented Phase 4.5 collision influence spreading. `GoalNetMesh` now builds `particleNeighbors` from deduplicated structural edges and logs average neighbor count. `GoalNetSimulator` adds `enableCollisionInfluenceSpread`, `collisionSpreadStrength`, `collisionSpreadRings`, `collisionSpreadFalloff`, and `collisionSpreadReactionScale`. Direct ball-sphere particle projections now spread a decayed correction through 1-3 structural-edge rings (realtime default: 2 rings, 0.35 first-ring strength, 0.5 falloff) so the ball impact forms a wider pocket instead of only moving the directly hit particles. Spread corrections contribute only partially to ball reaction impulse (default 0.2) to avoid making the tuned Phase 4 catch response too stiff. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Next Unity validation: compare pocket size/FPS with influence spread on/off, then tune `collisionSpreadStrength` and `collisionSpreadRings` before moving to swept sphere-vs-edge/triangle collision.
- 2026-05-12: Implemented Phase 4.6 soft catch reaction. `GoalNetSimulator` now has `enableSoftCatchReaction`, `elasticReactionScale`, `catchVelocityDamping`, and `maxContactReboundSpeed`. When enabled, ball reaction is built as a small elastic component from accumulated particle correction plus a velocity-opposing damping impulse capped by current ball momentum. The result is then clamped so the ball cannot leave contact faster than `maxContactReboundSpeed` along the reaction direction. Realtime defaults: elastic 0.18, damping 0.65, max rebound 1.5 m/s; high-quality defaults: elastic 0.22, damping 0.75, max rebound 2 m/s. Legacy Phase 4 impulse behavior remains available by disabling `enableSoftCatchReaction`. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Unity validation target: ball should press deeper into the net and lose speed without being immediately kicked away; if still too bouncy, lower `elasticReactionScale` or `maxContactReboundSpeed`; if it tunnels or never recovers, raise `catchVelocityDamping`, `maxReactionImpulse`, or elastic slightly.
- 2026-05-12: User validated Phase 4.6: ball no longer immediately rebounds, but it feels sticky and drops while the net still only shakes lightly instead of being driven deeply backward. Implemented Phase 4.7 ball impact drive. `GoalNetSimulator` adds `enableBallImpactDrive`, `ballImpactDriveStrength`, `ballImpactDriveMaxStep`, `ballImpactDriveRings`, `ballImpactDriveFalloff`, and `ballImpactDriveReactionScale`. During each direct ball-particle collision, contacted particles and nearby structural-edge rings receive a capped displacement in the current ball-velocity direction, so the contact patch is actively carried into the net rather than only being projected to the sphere surface. Realtime defaults: strength 0.45, max step 0.035m per solver pass, 2 rings, falloff 0.55, reaction scale 0.05; also lowered realtime `catchVelocityDamping` from 0.65 to 0.45 so the ball keeps enough forward momentum to drive a deeper pocket. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Unity validation: if deformation is still shallow, raise `ballImpactDriveStrength` to 0.6-0.8 or `ballImpactDriveMaxStep` to 0.05; if the net is dragged too far or looks smeared, lower strength/max step first.
- 2026-05-12: User tried the Phase 4.7 tuning and confirmed deformation is larger than before but still not a deep soccer-net compression. Conclusion: further tuning alone is not enough because direct particle-vs-sphere contacts are too sparse/local. Implemented Phase 4.8 pocket pressure field. `GoalNetSimulator` adds `enablePocketPressureField`, `pocketPressureRadius`, `pocketPressureStrength`, `pocketPressureMaxStep`, `pocketPressureFalloffPower`, and `pocketPressureReactionScale`. When a solver pass has at least one direct ball-particle contact, nearby particles inside `solverRadius + pocketPressureRadius` receive a distance-falloff displacement in the current ball velocity direction. Realtime defaults: radius 0.55m, strength 0.9, max step 0.08m per solver pass, falloff power 1.0, reaction scale 0.04. High-quality defaults: radius 0.65m, strength 1.1, max step 0.1m, falloff power 0.9, reaction scale 0.05. `dotnet build GoalNetXPBD.Runtime.csproj` passes. Unity validation: first compare pressure field on/off; if still shallow, raise `pocketPressureRadius` to 0.8-1.0 and `pocketPressureStrength` to 1.2-1.5; if the whole net translates unnaturally or FPS drops, reduce radius first. Longer-term physically cleaner solution remains swept sphere-vs-edge/triangle collision plus persistent contact patches.
- 2026-05-14: Implemented first-pass ground contact for net particles. `GoalNetSimulator` now has `enableGroundCollision`, optional `groundTransform`, `groundHeight`, `groundSkin`, `groundFriction`, and `groundBounce`. Ground contact is solved as a horizontal plane projection inside the XPBD iteration loop after ball collision, so distance/bending/ball corrections cannot leave free particles below the floor for the rest of the solve. Contacted particles also get vertical velocity response and horizontal friction after velocity recovery, helping the loose bottom net drag and gather on the floor instead of falling through it. Realtime preset uses friction 0.35; high-quality uses 0.45; bounce defaults to 0. `dotnet build GoalNetXPBD.Runtime.csproj` passes.
- 2026-05-14: User tuned Soft Catch Reaction in Unity and asked to make the values default: `elasticReactionScale=0.905`, `catchVelocityDamping=0.878`, `maxContactReboundSpeed=0.52`. Updated field defaults plus both realtime and high-quality presets in `GoalNetSimulator`.
- 2026-05-14: Started high-speed anti-tunneling work. Added swept sphere collision to `GoalNetSimulator`: `enableSweptBallCollision`, `enableSweptEdgeCollision`, `sweptCollisionSkin`, `sweptCollisionMaxCorrection`, and `sweptCollisionReactionScale`. The simulator now stores the ball center from the previous FixedUpdate and interpolates a swept segment for each substep. Each solver iteration checks free particles against the swept sphere capsule and, when enabled, checks structural net edges using closest segment-to-segment distance, distributing edge corrections to the two endpoint particles. Swept contacts also feed collision spread, impact drive, pocket pressure, and ball reaction with a bounded scale. Realtime preset enables particle+edge sweep with skin 0.02m, max correction 0.12m, reaction scale 0.35; high-quality uses 0.025m, 0.16m, 0.4. Target: reduce tunneling for 45-50 m/s shots that pass between sparse particle contacts.
- 2026-05-14: User tested swept collision and found contact FPS dropped from ~80 to 3-4. Root cause was swept edge collision running over all structural edges every solver iteration and each edge contact triggering neighbor-spread BFS. Added performance gates: `sweptCollisionPassesPerSubstep` (default 2), `maxSweptEdgeContactsPerPass` (realtime 24, high-quality 40), a swept AABB broadphase reject before segment-distance math, and `sweptEdgeInfluenceSpread` default false. Particle swept contacts still spread/drive the pocket; edge swept contacts now mainly prevent tunneling without causing a contact-frame BFS storm.
- 2026-05-14: Implemented Phase 4.10 bottom soft tether / drag-on-ground. `GoalNetSimulator` now auto-detects the lowest free rest-pose particles within `bottomDetectHeight` and caches them as the bottom/tail region. Added `enableBottomSoftTether`, `bottomDetectHeight`, `bottomTetherCompliance`, `bottomTetherMaxDistance`, `bottomMaxLiftHeight`, and `bottomGroundFriction`, plus context menu `Rebuild Bottom Soft Tethers`. The tether solves inside the XPBD iteration after ball collision and before ground collision, softly pulling bottom particles toward their rest/floor area, applying optional max drift/lift caps, and boosting horizontal friction while they touch the ground. Realtime defaults: detect 0.18m, compliance 0.002, max distance 0.75m, max lift 0.45m, bottom friction 0.75. High-quality defaults: detect 0.2m, compliance 0.0015, max distance 0.85m, max lift 0.5m, bottom friction 0.8.
- 2026-05-14: User tuned 45 m/s catch/release to avoid the ball sticking and rolling down the net. Updated defaults and both presets: `maxReactionImpulse=10`, `elasticReactionScale=0.813`, `catchVelocityDamping=0.573`, `maxContactReboundSpeed=1.5`, with `velocityOpposingImpulseScale` still 1.

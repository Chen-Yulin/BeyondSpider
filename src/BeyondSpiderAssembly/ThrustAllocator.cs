using System.Collections.Generic;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // 6-DOF control allocation for a ship's enrolled thrusters (ADR-0018 §6).
    //
    // Each thruster contributes one column of a 6×N matrix B — three force rows and three torque
    // rows (its lever arm about the hull's live center of mass) — and the player's command is a
    // 6-vector wrench w. Thrusters push only, never pull, so the problem is a least-squares solve
    // under a one-sided box constraint:
    //
    //     minimize ||B·u - w||   subject to   u in [0,1]^N
    //
    // Solved by regularized normal equations plus clamp-and-resolve: take the unconstrained
    // least-squares step, clamp whatever left [0,1], freeze those columns, re-solve the residual
    // against the rest, three rounds. B·Bᵀ is only 6×6, so the linear solve is a hand-rolled
    // Gaussian elimination and no matrix library is involved; per-frame cost is O(36N) — a few
    // hundred multiplies at N=20, nothing at 50 Hz.
    //
    // The λI term is the load-bearing part, not numerical fastidiousness. Players WILL build
    // thrust-degenerate ships (every nozzle parallel, so B·Bᵀ is singular). With regularization an
    // unreachable direction simply gets zero output and least-squares returns the closest wrench
    // the ship can actually produce: maneuvers you didn't build for don't happen, instead of
    // NaN-ing the hull into orbit. That graceful degradation is free, and it is also what makes
    // "lose the starboard bank and keep flying on the survivors" work with no special-case code.
    public sealed class ThrustAllocator
    {
        public const int Dof = 6;

        // Torque rows are weighted so attitude wins over translation when the ship saturates.
        // Flying crooked is recoverable; not being able to turn is not. Row weighting is the exact
        // right tool here — min ||W(Bu - w)|| with W diagonal is still a plain least-squares
        // problem, so this costs one multiply per torque row and nothing at solve time.
        public const float TorqueWeight = 3f;

        // Regularization RELATIVE to the normal matrix's own scale, so it stays meaningful whether
        // the ship carries two small nozzles or twenty huge ones. At 1e-4 a well-conditioned axis
        // is damped by ~6e-4 of its own eigenvalue (invisible) while a null direction is bounded
        // instead of exploding.
        private const float RelativeRegularization = 1e-4f;
        private const int MaxRounds = 3;

        private readonly List<ThrusterBlock> enrolled = new List<ThrusterBlock>();
        private float[,] columns = new float[0, 0];
        private float[] solution = new float[0];
        private bool[] clamped = new bool[0];

        private readonly float[] target = new float[Dof];
        private readonly float[] residual = new float[Dof];
        private readonly float[] step = new float[Dof];
        private readonly float[,] normal = new float[Dof, Dof];
        private readonly float[,] work = new float[Dof, Dof + 1];

        // Bumped whenever the enrolled set may have changed (a thruster registered, unregistered
        // or detonated). The roster is rebuilt from ship.Thrusters every tick anyway — this only
        // forces the deterministic re-sort, which is what keeps host and client agreeing on each
        // thruster's slot index in the replicated output batch.
        private bool rosterDirty = true;
        private int sortedCount = -1;

        public int Count { get { return enrolled.Count; } }

        public ThrusterBlock At(int index)
        {
            return enrolled[index];
        }

        public float Output(int index)
        {
            return solution[index];
        }

        public void MarkRosterDirty()
        {
            rosterDirty = true;
        }

        // Rebuilds the enrolled list (advanced-control thrusters that are still alive) and their
        // wrench columns. Done every tick rather than cached behind an invalidation flag: it is a
        // cross product and a few multiplies per thruster, and doing it unconditionally means the
        // live center of mass, a detonated nozzle and a shifted hull are all picked up correctly
        // with no invalidation logic to get wrong.
        private void RebuildColumns(ShipState ship, Vector3 centerOfMass)
        {
            enrolled.Clear();
            for (int i = 0; i < ship.Thrusters.Count; i++)
            {
                ThrusterBlock thruster = ship.Thrusters[i];
                if (thruster != null && thruster.IsAlive && thruster.Advanced)
                {
                    enrolled.Add(thruster);
                }
            }

            if (rosterDirty || enrolled.Count != sortedCount)
            {
                // Deterministic slot order across machines (same reason ShipPartition sorts cores
                // and captains by Guid): the replicated output batch addresses thrusters by index.
                enrolled.Sort(CompareThrusters);
                sortedCount = enrolled.Count;
                rosterDirty = false;
            }

            int n = enrolled.Count;
            if (solution.Length != n)
            {
                columns = new float[Mathf.Max(1, n), Dof];
                solution = new float[n];
                clamped = new bool[n];
            }

            for (int i = 0; i < n; i++)
            {
                Vector3 force;
                Vector3 torque;
                enrolled[i].GetWrench(centerOfMass, out force, out torque);
                columns[i, 0] = force.x;
                columns[i, 1] = force.y;
                columns[i, 2] = force.z;
                columns[i, 3] = torque.x * TorqueWeight;
                columns[i, 4] = torque.y * TorqueWeight;
                columns[i, 5] = torque.z * TorqueWeight;
            }
        }

        private static int CompareThrusters(ThrusterBlock a, ThrusterBlock b)
        {
            return a.GuidHash.CompareTo(b.GuidHash);
        }

        // Solves for the enrolled thrusters' output ratios. Returns the number of enrolled
        // thrusters; read the answers back with Output(i)/At(i).
        public int Solve(ShipState ship, Vector3 forceCommand, Vector3 torqueCommand, Vector3 centerOfMass)
        {
            RebuildColumns(ship, centerOfMass);
            int n = enrolled.Count;
            if (n == 0)
            {
                return 0;
            }

            target[0] = forceCommand.x;
            target[1] = forceCommand.y;
            target[2] = forceCommand.z;
            target[3] = torqueCommand.x * TorqueWeight;
            target[4] = torqueCommand.y * TorqueWeight;
            target[5] = torqueCommand.z * TorqueWeight;

            for (int i = 0; i < n; i++)
            {
                solution[i] = 0f;
                clamped[i] = false;
            }

            for (int round = 0; round < MaxRounds; round++)
            {
                // Residual the still-free columns have to cover: the command minus whatever the
                // already-frozen ones are contributing at their clamped values.
                for (int r = 0; r < Dof; r++)
                {
                    residual[r] = target[r];
                }
                int freeCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!clamped[i])
                    {
                        freeCount++;
                        continue;
                    }
                    float value = solution[i];
                    if (value == 0f)
                    {
                        continue;
                    }
                    for (int r = 0; r < Dof; r++)
                    {
                        residual[r] -= value * columns[i, r];
                    }
                }
                if (freeCount == 0)
                {
                    break;
                }

                BuildNormalMatrix(n);
                if (!SolveSixBySix(normal, residual, step))
                {
                    break; // regularization should make this unreachable; bail rather than emit NaN
                }

                bool violated = false;
                for (int i = 0; i < n; i++)
                {
                    if (clamped[i])
                    {
                        continue;
                    }
                    float value = 0f;
                    for (int r = 0; r < Dof; r++)
                    {
                        value += columns[i, r] * step[r];
                    }
                    if (value < 0f)
                    {
                        solution[i] = 0f;
                        clamped[i] = true;
                        violated = true;
                    }
                    else if (value > 1f)
                    {
                        solution[i] = 1f;
                        clamped[i] = true;
                        violated = true;
                    }
                    else
                    {
                        solution[i] = value;
                    }
                }
                if (!violated)
                {
                    break;
                }
            }

            return n;
        }

        // B_free · B_freeᵀ + λI, with λ scaled off the matrix's own trace.
        private void BuildNormalMatrix(int n)
        {
            for (int r = 0; r < Dof; r++)
            {
                for (int c = 0; c < Dof; c++)
                {
                    normal[r, c] = 0f;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (clamped[i])
                {
                    continue;
                }
                for (int r = 0; r < Dof; r++)
                {
                    float value = columns[i, r];
                    if (value == 0f)
                    {
                        continue;
                    }
                    for (int c = 0; c < Dof; c++)
                    {
                        normal[r, c] += value * columns[i, c];
                    }
                }
            }

            float trace = 0f;
            for (int r = 0; r < Dof; r++)
            {
                trace += normal[r, r];
            }
            float lambda = Mathf.Max(1e-6f, trace / Dof * RelativeRegularization);
            for (int r = 0; r < Dof; r++)
            {
                normal[r, r] += lambda;
            }
        }

        // Gaussian elimination with partial pivoting on the 6×6 system. Hand-rolled because the
        // system is fixed-size and tiny — pulling in a matrix library for this would cost more
        // than it saves.
        private bool SolveSixBySix(float[,] a, float[] b, float[] x)
        {
            for (int r = 0; r < Dof; r++)
            {
                for (int c = 0; c < Dof; c++)
                {
                    work[r, c] = a[r, c];
                }
                work[r, Dof] = b[r];
            }

            for (int col = 0; col < Dof; col++)
            {
                int pivot = col;
                float best = Mathf.Abs(work[col, col]);
                for (int r = col + 1; r < Dof; r++)
                {
                    float magnitude = Mathf.Abs(work[r, col]);
                    if (magnitude > best)
                    {
                        best = magnitude;
                        pivot = r;
                    }
                }
                if (best < 1e-12f)
                {
                    return false;
                }
                if (pivot != col)
                {
                    for (int c = col; c <= Dof; c++)
                    {
                        float swap = work[col, c];
                        work[col, c] = work[pivot, c];
                        work[pivot, c] = swap;
                    }
                }

                float diagonal = work[col, col];
                for (int r = col + 1; r < Dof; r++)
                {
                    float factor = work[r, col] / diagonal;
                    if (factor == 0f)
                    {
                        continue;
                    }
                    for (int c = col; c <= Dof; c++)
                    {
                        work[r, c] -= factor * work[col, c];
                    }
                }
            }

            for (int r = Dof - 1; r >= 0; r--)
            {
                float sum = work[r, Dof];
                for (int c = r + 1; c < Dof; c++)
                {
                    sum -= work[r, c] * x[c];
                }
                x[r] = sum / work[r, r];
            }
            return true;
        }
    }

    // Per-ship propulsion tick (ADR-0018). Runs host-only from SpaceCombatRuntime.FixedUpdate,
    // which is deliberate: thrust cannot be solved block-by-block, since the allocator has to see
    // every enrolled nozzle at once to split one commanded wrench between them. It also means
    // there is exactly ONE place in the mod that calls AddForce for propulsion, and it sits behind
    // the authority fence — the single most dangerous thing to get wrong here would be a client
    // applying its own thrust and fighting the host's position corrections (ADR-0015).
    public static class ShipThrustControl
    {
        // How often the cached distinct-rigidbody list is rebuilt (ADR-0018). COM and mass are
        // recomputed from that list every tick; only a change in which rigidbodies EXIST (a welded
        // cluster splitting, a body newly created) waits this long to be reflected.
        private const float ComRefreshInterval = 1f;

        // Full deflection on a rotation key asks for this body rate (rad/s ≈ 34°/s). Placeholder,
        // pending in-game feel.
        private const float CommandedRollRate = 0.6f;
        // Rate-loop gain, in torque per (rad/s of error) per unit ship mass. The loop is a plain
        // proportional controller on body rate — releasing the key commands zero rate, which is
        // what produces the automatic de-spin. Placeholder, pending in-game feel.
        private const float AttitudeRateGain = 25f;

        private static readonly HashSet<Rigidbody> bodyScratch = new HashSet<Rigidbody>();

        public static void Tick(ShipState ship, float deltaTime)
        {
            if (!NetAuthority.IsAuthority || ship.Thrusters.Count == 0)
            {
                return;
            }

            if (ship.ThrustCutoff)
            {
                // Emergency cutoff: everything dark, and no energy spent. Deliberately unconditional
                // — this is the player's one guaranteed way to stop the ship spending its grid on
                // propulsion, so it must not depend on a captain being alive to honor it.
                for (int i = 0; i < ship.Thrusters.Count; i++)
                {
                    ThrusterBlock thruster = ship.Thrusters[i];
                    if (thruster != null)
                    {
                        thruster.CurrentOutput = 0f;
                    }
                }
                ThrusterNet.BroadcastOutputs(ship);
                return;
            }

            // Key-controlled thrusters run themselves — no captain required, so a captainless hull
            // is still flyable as a dumb tug. Deliberately BEFORE the center-of-mass work, which
            // only the allocator needs: a captainless ship never pays for a COM sweep at all.
            for (int i = 0; i < ship.Thrusters.Count; i++)
            {
                ThrusterBlock thruster = ship.Thrusters[i];
                if (thruster != null && !thruster.Advanced)
                {
                    thruster.DriveThrust(ship, thruster.KeyHeld ? 1f : 0f, deltaTime);
                }
            }

            SpaceCaptainBlock captain = ship.Captain;
            if (captain == null)
            {
                // Enrolled but unattended: nobody is commanding them, so they idle. The block's
                // own MInfo line is what tells the player why.
                for (int i = 0; i < ship.Thrusters.Count; i++)
                {
                    ThrusterBlock thruster = ship.Thrusters[i];
                    if (thruster != null && thruster.Advanced)
                    {
                        thruster.CurrentOutput = 0f;
                    }
                }
                ThrusterNet.BroadcastOutputs(ship);
                return;
            }

            float totalMass;
            Vector3 centerOfMass = ResolveCenterOfMass(ship, out totalMass);

            Vector3 force;
            Vector3 torque;
            BuildCommandWrench(ship, captain, totalMass, out force, out torque);

            int count = ship.Allocator.Solve(ship, force, torque, centerOfMass);
            for (int i = 0; i < count; i++)
            {
                ship.Allocator.At(i).DriveThrust(ship, ship.Allocator.Output(i), deltaTime);
            }

            ThrusterNet.BroadcastOutputs(ship);
        }

        // Translation is an open-loop FORCE command and rotation is a closed-loop RATE command
        // (ADR-0018 §5). The asymmetry is psychological, not physical: releasing the throttle
        // should leave you coasting (drifting is the point of space flight), releasing the stick
        // should leave you steady (tumbling is nausea). It costs nothing, because both halves end
        // up as components of the same 6-vector and only differ in who authored them.
        private static void BuildCommandWrench(ShipState ship, SpaceCaptainBlock captain, float totalMass,
            out Vector3 force, out Vector3 torque)
        {
            Transform frame = captain.transform;

            // Ship axes in the captain's own frame (ADR-0018, user-confirmed): the block sits flat
            // on the deck, so its forward points UP out of the hull and its up points AFT.
            //   ship right   =  captain.right
            //   ship up      =  captain.forward
            //   ship forward = -captain.up      (captain.up is the ship's AFT direction)
            Vector3 shipRight = frame.right;
            Vector3 shipUp = frame.forward;
            Vector3 shipForward = -frame.up;

            // --- Translation: open-loop force (release = coast, ADR-0018 §5) ---
            // DriveTranslation is (strafe-right, ascend, forward). Forward is the ship's nose,
            // i.e. -captain.up — the earlier `+frame.up` mapping drove fore/aft backwards.
            Vector3 t = ship.DriveTranslation;
            Vector3 translationDir = shipRight * t.x + shipUp * t.y + shipForward * t.z;
            if (translationDir.sqrMagnitude > 1f)
            {
                translationDir.Normalize(); // a diagonal command must not out-ask a straight one
            }
            force = translationDir * TotalThrustAuthority(ship);

            // Desired angular velocity, built from the ship axes by cross product so the sign is
            // physically grounded and matches rigidbody.angularVelocity (v = ω × r, standard
            // Vector3.Cross): the rotation that carries axis A toward axis B is ω = A × B.
            //   pitch up  : nose toward ship up   →  shipForward × shipUp   (this is what fixes the
            //                                                                reversed pitch — it
            //                                                                comes out as -captain.right,
            //                                                                the opposite of the old
            //                                                                +captain.right mapping)
            //   yaw right : nose toward ship right →  shipForward × shipRight
            //   roll right: top  toward ship right →  shipUp      × shipRight
            // NOTE: if an axis still reads reversed in-game, flip the sign of just that term — the
            // control loop itself is proven correct by the working de-spin, so a per-axis flip is
            // safe and self-contained.
            Vector3 r = ship.DriveRotation;
            Vector3 rotationDir =
                  Vector3.Cross(shipForward, shipUp) * r.x    // pitch
                + Vector3.Cross(shipForward, shipRight) * r.y // yaw
                + Vector3.Cross(shipUp, shipRight) * r.z;     // roll
            if (rotationDir.sqrMagnitude > 1f)
            {
                rotationDir.Normalize();
            }

            if (!ship.AttitudeHold)
            {
                // Stabilizer off: pure open-loop torque, no de-spin. The escape hatch for players
                // who want to fly it by hand, and for "don't waste grid power holding attitude".
                // Authority is force × a nominal lever arm taken from the measured hull box, which
                // is the only length scale available here that tracks how big the ship actually is.
                float nominalArm = Mathf.Max(1f, ship.HullSize.magnitude * 0.5f);
                torque = rotationDir * (TotalThrustAuthority(ship) * nominalArm);
                return;
            }

            Rigidbody coreBody = ship.Core != null ? ship.Core.GetComponent<Rigidbody>() : null;
            Vector3 currentRate = coreBody != null ? coreBody.angularVelocity : Vector3.zero;
            Vector3 rateError = rotationDir * CommandedRollRate - currentRate;
            torque = rateError * (AttitudeRateGain * totalMass);
        }

        // "Everything you've got in that direction": the sum of every enrolled nozzle's ceiling.
        // The least-squares solve then returns the closest wrench the ship can actually reach, so
        // over-asking is harmless and under-asking would silently cap the ship's acceleration.
        private static float TotalThrustAuthority(ShipState ship)
        {
            float sum = 0f;
            for (int i = 0; i < ship.Thrusters.Count; i++)
            {
                ThrusterBlock thruster = ship.Thrusters[i];
                if (thruster != null && thruster.IsAlive && thruster.Advanced)
                {
                    sum += thruster.MaxThrust * thruster.RatioCap;
                }
            }
            return sum;
        }

        // Mass-weighted center of mass, recomputed every tick from the cached rigidbody list — the
        // list rebuild (the expensive GetComponent sweep) is the only part throttled to once a
        // second. A body destroyed since the last rebuild reads as Unity-null and simply drops out,
        // so losing a block registers immediately even though the list still holds its slot.
        private static Vector3 ResolveCenterOfMass(ShipState ship, out float totalMass)
        {
            if (!ship.BodiesValid || Time.time >= ship.NextBodyRefresh)
            {
                ship.NextBodyRefresh = Time.time + ComRefreshInterval;
                RefreshBodyList(ship);
                ship.BodiesValid = true;
            }

            Vector3 weighted = Vector3.zero;
            totalMass = 0f;
            List<Rigidbody> bodies = ship.ThrustBodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                Rigidbody body = bodies[i];
                if (body == null)
                {
                    continue;
                }
                weighted += body.worldCenterOfMass * body.mass;
                totalMass += body.mass;
            }

            if (totalMass <= 0.0001f)
            {
                totalMass = 1f;
                return ship.Core != null ? ship.Core.transform.position : Vector3.zero;
            }
            return weighted / totalMass;
        }

        // Collects the hull's DISTINCT rigidbodies into the ship's cache. Blocks welded into one
        // SimCluster share a single rigidbody whose mass is already their sum, so deduplicating by
        // rigidbody reference both avoids double counting and gets the welded mass right for free.
        private static void RefreshBodyList(ShipState ship)
        {
            ship.ThrustBodies.Clear();
            bodyScratch.Clear();
            for (int i = 0; i < ship.Blocks.Count; i++)
            {
                BlockBehaviour block = ship.Blocks[i];
                if (block == null || !block.isSimulating)
                {
                    continue;
                }
                Rigidbody body = block.GetComponent<Rigidbody>();
                if (body == null || !bodyScratch.Add(body))
                {
                    continue;
                }
                ship.ThrustBodies.Add(body);
            }
        }
    }
}

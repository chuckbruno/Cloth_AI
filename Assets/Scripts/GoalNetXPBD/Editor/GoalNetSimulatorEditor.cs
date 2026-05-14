using GoalNetXPBD;
using UnityEditor;
using UnityEngine;

namespace GoalNetXPBD.EditorTools
{
    [CustomEditor(typeof(GoalNetSimulator))]
    public class GoalNetSimulatorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            GoalNetSimulator simulator = (GoalNetSimulator)target;

            DrawPresetPanel(simulator);
            DrawRuntimeStats(simulator);

            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        private void DrawPresetPanel(GoalNetSimulator simulator)
        {
            EditorGUILayout.LabelField("Goal Net Presets", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Realtime"))
                    {
                        ApplyPreset(simulator, simulator.ApplyRealtimePreviewSettings);
                    }

                    if (GUILayout.Button("Stable 45mps"))
                    {
                        ApplyPreset(simulator, simulator.ApplyStable45mpsCatchSettings);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("High Speed 50mps"))
                    {
                        ApplyPreset(simulator, simulator.ApplyHighSpeed50mpsCatchSettings);
                    }

                    if (GUILayout.Button("Loose Net Pile"))
                    {
                        ApplyPreset(simulator, simulator.ApplyLooseNetPileSettings);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("High Quality"))
                    {
                        ApplyPreset(simulator, simulator.ApplyHighQualitySettings);
                    }

                    if (GUILayout.Button("Debug Gizmos"))
                    {
                        ApplyPreset(simulator, simulator.ApplyDebugVisualizationSettings);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset Rest Pose"))
                    {
                        simulator.ResetToRest();
                    }

                    if (GUILayout.Button("Rebuild Bottom"))
                    {
                        simulator.RebuildBottomSoftTethers();
                    }
                }
            }
        }

        private void DrawRuntimeStats(GoalNetSimulator simulator)
        {
            EditorGUILayout.LabelField("Runtime Debug", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox("Enter Play Mode to see live contact and impulse stats.", MessageType.Info);
                }

                EditorGUILayout.LabelField("Ball Speed", $"{simulator.LastBallSpeed:0.00} m/s");
                EditorGUILayout.LabelField("Last Ball Impulse", $"{simulator.LastAppliedBallImpulse.magnitude:0.000} N*s");
                EditorGUILayout.LabelField("Direct Contacts", simulator.LastDirectContactCount.ToString());
                EditorGUILayout.LabelField("Swept Particle Contacts", simulator.LastSweptParticleContactCount.ToString());
                EditorGUILayout.LabelField("Swept Edge Contacts", simulator.LastSweptEdgeContactCount.ToString());
                EditorGUILayout.LabelField("Persistent Patch Particles", simulator.LastPersistentPatchCount.ToString());
                EditorGUILayout.LabelField("Patch Solve Contacts", simulator.LastPatchSolveContactCount.ToString());
                EditorGUILayout.LabelField("Bottom Tether Particles", simulator.BottomTetherCount.ToString());
                EditorGUILayout.LabelField("Particles / Edges / Bending", $"{simulator.ParticleCount} / {simulator.EdgeCount} / {simulator.BendingConstraintCount}");
            }

            if (Application.isPlaying)
            {
                Repaint();
            }
        }

        private void ApplyPreset(GoalNetSimulator simulator, System.Action apply)
        {
            Undo.RecordObject(simulator, "Apply Goal Net Preset");
            apply();
            EditorUtility.SetDirty(simulator);
        }
    }
}

using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GoalNetXPBD.EditorTools
{
    /// <summary>
    /// One-click setup of the launch UI inside the currently open scene.
    /// Menu: Tools/Cloth_AI/Setup Launch Button In Scene
    ///
    /// What it does:
    ///  - Ensures an EventSystem exists.
    ///  - Creates (or reuses) a Screen-Space-Overlay Canvas named "GoalNetXPBD_UI".
    ///  - Adds a "Launch Ball" Button anchored to the bottom-right corner.
    ///  - Finds a BallLauncher in the scene (warns if none) and persistently wires
    ///    the Button's OnClick to BallLauncher.LaunchBall().
    ///  - Marks the scene dirty so Unity prompts you to save.
    /// </summary>
    public static class BallLauncherUIInstaller
    {
        private const string CanvasName = "GoalNetXPBD_UI";
        private const string ButtonName = "LaunchBallButton";

        [MenuItem("Tools/Cloth_AI/Setup Launch Button In Scene")]
        public static void SetupLaunchButton()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog(
                    "Cloth_AI",
                    "No scene is currently loaded. Open ClothSim.unity first, then run this menu again.",
                    "OK");
                return;
            }

            // --- 1. EventSystem ---
            EventSystem eventSystem = Object.FindObjectOfType<EventSystem>();
            if (eventSystem == null)
            {
                GameObject esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
                SceneManager.MoveGameObjectToScene(esGo, scene);
            }

            // --- 2. Canvas ---
            Canvas canvas = FindCanvasByName(CanvasName);
            if (canvas == null)
            {
                GameObject canvasGo = new GameObject(
                    CanvasName,
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
                SceneManager.MoveGameObjectToScene(canvasGo, scene);

                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1000;

                CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
            }

            // --- 3. Button ---
            Transform existingBtn = canvas.transform.Find(ButtonName);
            GameObject buttonGo;
            if (existingBtn != null)
            {
                buttonGo = existingBtn.gameObject;
            }
            else
            {
                buttonGo = new GameObject(
                    ButtonName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image),
                    typeof(Button));
                Undo.RegisterCreatedObjectUndo(buttonGo, "Create Launch Button");
                buttonGo.transform.SetParent(canvas.transform, worldPositionStays: false);

                RectTransform rt = buttonGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-40f, 40f);
                rt.sizeDelta = new Vector2(220f, 80f);

                Image img = buttonGo.GetComponent<Image>();
                img.color = new Color(0.15f, 0.55f, 0.95f, 1f);

                // Label child
                GameObject labelGo = new GameObject(
                    "Label",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text));
                labelGo.transform.SetParent(buttonGo.transform, worldPositionStays: false);
                RectTransform lrt = labelGo.GetComponent<RectTransform>();
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero;
                lrt.offsetMax = Vector2.zero;

                Text label = labelGo.GetComponent<Text>();
                label.text = "Launch Ball";
                label.alignment = TextAnchor.MiddleCenter;
                label.color = Color.white;
                label.fontStyle = FontStyle.Bold;
                label.fontSize = 28;
                // Try to use Unity's built-in Arial; fall back gracefully.
                Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (defaultFont == null) defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                label.font = defaultFont;
            }

            Button button = buttonGo.GetComponent<Button>();

            // --- 4. Find a BallLauncher to wire to ---
            BallLauncher launcher = Object.FindObjectOfType<BallLauncher>();
            if (launcher == null)
            {
                Debug.LogWarning(
                    "[Cloth_AI] No BallLauncher found in the scene. The button has been created, but its OnClick is not wired.\n" +
                    "Add a BallLauncher to the scene and run this menu again, or wire OnClick manually.");
            }
            else
            {
                // Remove any previously-persisted listeners that target this method, then add a fresh one.
                int count = button.onClick.GetPersistentEventCount();
                for (int i = count - 1; i >= 0; i--)
                {
                    Object t = button.onClick.GetPersistentTarget(i);
                    string m = button.onClick.GetPersistentMethodName(i);
                    if (t is BallLauncher && m == nameof(BallLauncher.LaunchBall))
                    {
                        UnityEventTools.RemovePersistentListener(button.onClick, i);
                    }
                }
                UnityEventTools.AddPersistentListener(button.onClick, launcher.LaunchBall);
                button.onClick.SetPersistentListenerState(
                    button.onClick.GetPersistentEventCount() - 1,
                    UnityEngine.Events.UnityEventCallState.RuntimeOnly);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = buttonGo;

            Debug.Log("[Cloth_AI] Launch UI installed. Save the scene to keep the changes.");
        }

        private static Canvas FindCanvasByName(string name)
        {
            Canvas[] all = Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.name == name) return all[i];
            }
            return null;
        }
    }
}

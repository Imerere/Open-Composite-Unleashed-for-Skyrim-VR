using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace OpenCompositeConfigurator
{
    // ═══════════════════════════════════════════════════════════════════════
    // BINDINGS TAB: controller photo switcher + draggable dot calibration.
    //
    // The hex codes are the same for every controller model because OCU
    // translates all of them to the same OpenVR legacy button ids. What
    // changes per model is only the photo and where the dots sit on it.
    // The controlmap writer stamps every VR column pair (Vive 4/5,
    // Oculus 6/7, WMR 8/9) so bindings apply no matter which controller
    // type the runtime reports (Index reports "knuckles" and reads the
    // Vive columns, which is why bindings used to silently not apply).
    //
    // Move Dots mode: drag any dot to the right spot on the photo; the
    // layout persists to ControllerDotLayouts.json beside the EXE so we
    // can bake the final coordinates into defaults later.
    // ═══════════════════════════════════════════════════════════════════════
    public partial class MainForm
    {
        private Image? _knucklesImage;
        private ComboBox _cmbControllerModel = null!;
        private CheckBox _chkMoveDots = null!;
        private string _controllerModelKey = "touch";
        private string? _dragCtrlButton;

        // Trackpad swipe shortcut controls (Settings tab, Index-only)
        private Label _lblTrackpadSwipe = null!;
        private ComboBox _cmbTrackpadSwipe = null!;
        private Label _lblTrackpadSwipeHint = null!;

        // Active layout the paint/hit-test/click handlers use. Starts as a
        // copy of the Touch defaults; swapped when the model changes.
        private Dictionary<string, (string display, PointF pos, bool isStickDir)> _activeControllerButtons = new();

        // Index knuckles defaults, baked from the hand-calibrated layout
        // (ControllerDotLayouts.json, 2026-07-04). The JSON override still
        // wins if the user recalibrates with Move Dots.
        private static readonly Dictionary<string, (string display, PointF pos, bool isStickDir)> ControllerButtonsKnuckles = new()
        {
            // Left controller
            { "left_stick",  ("L Stick Click", new PointF(0.236f, 0.101f), false) },
            { "x_button",    ("A Button",      new PointF(0.332f, 0.167f), false) },
            { "y_button",    ("B Button",      new PointF(0.347f, 0.111f), false) },
            { "l_trigger",   ("L Trigger",     new PointF(0.412f, 0.061f), false) },
            { "l_grip",      ("L Grip Squeeze", new PointF(0.327f, 0.472f), false) },
            // Right controller
            { "right_stick", ("R Stick Click", new PointF(0.751f, 0.111f), false) },
            { "a_button",    ("A Button",      new PointF(0.657f, 0.178f), false) },
            { "b_button",    ("B Button",      new PointF(0.639f, 0.125f), false) },
            { "r_trigger",   ("R Trigger",     new PointF(0.589f, 0.072f), false) },
            { "r_grip",      ("R Grip Squeeze", new PointF(0.671f, 0.469f), false) },
            // Index trackpad: the touch oval under the stick, Touch has no
            // equivalent. Click acts as A (lower half) / B-Menu (upper half),
            // or as the VRIK gesture input when VRIK Knuckles support is on.
            { "l_trackpad",  ("L Trackpad",    new PointF(0.297f, 0.133f), false) },
            { "r_trackpad",  ("R Trackpad",    new PointF(0.697f, 0.151f), false) },
            // Left stick directions
            { "left_stick_up",    ("L Stick Up",    new PointF(0.238f, 0.056f), true) },
            { "left_stick_down",  ("L Stick Down",  new PointF(0.235f, 0.146f), true) },
            { "left_stick_left",  ("L Stick Left",  new PointF(0.209f, 0.103f), true) },
            { "left_stick_right", ("L Stick Right", new PointF(0.262f, 0.103f), true) },
            // Right stick directions
            { "right_stick_up",    ("R Stick Up",    new PointF(0.749f, 0.074f), true) },
            { "right_stick_down",  ("R Stick Down",  new PointF(0.752f, 0.156f), true) },
            { "right_stick_left",  ("R Stick Left",  new PointF(0.719f, 0.109f), true) },
            { "right_stick_right", ("R Stick Right", new PointF(0.781f, 0.111f), true) },
        };

        private static string DotLayoutPath => Path.Combine(AppContext.BaseDirectory, "ControllerDotLayouts.json");
        private static string UiStatePath => Path.Combine(AppContext.BaseDirectory, "ConfiguratorUI.json");

        // The controller choice persists beside the EXE so an Index owner sees
        // their controller every launch until they switch back.
        private static string LoadUiModelChoice()
        {
            try
            {
                if (!File.Exists(UiStatePath)) return "touch";
                var state = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(UiStatePath));
                return state != null && state.TryGetValue("controllerModel", out var m) && m == "knuckles"
                    ? "knuckles" : "touch";
            }
            catch { return "touch"; }
        }

        private void SaveUiModelChoice()
        {
            try
            {
                File.WriteAllText(UiStatePath, JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["controllerModel"] = _controllerModelKey },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-fatal: choice just will not persist */ }
        }

        private void LoadKnucklesImage()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("OpenCompositeConfigurator.Resources.knuckles.png");
            if (stream != null)
                _knucklesImage = Image.FromStream(stream);
        }

        private Image? ActiveControllerImage =>
            _controllerModelKey == "knuckles" && _knucklesImage != null ? _knucklesImage : _controllerImage;

        internal static bool IsTrackpadButton(string? id) => id == "l_trackpad" || id == "r_trackpad";

        // Grips and triggers are big physical targets, they keep the full
        // Oculus circle size even on knuckles.
        private static bool IsFullSizeDot(string key) => key.Contains("grip") || key.Contains("trigger");

        // Knuckles face dots are half size: more inputs in a tighter cluster.
        // The Touch layout is locked in so only knuckles needed the change.
        private float DotRadiusFor(string key) =>
            _controllerModelKey == "knuckles" && !IsFullSizeDot(key) ? 7f : 14f;
        private float HitRadiusFor(string key, bool isStickDir) =>
            _controllerModelKey == "knuckles" && !IsFullSizeDot(key)
                ? (isStickDir ? 0.018f : 0.026f)
                : (isStickDir ? 0.025f : 0.04f);

        // Row under the controller photo: model dropdown + dot calibration toggle
        private void BuildControllerSwitcherRow(Control container, int x, int y, int width)
        {
            var lblModel = new Label
            {
                Text = "Controller",
                Location = new Point(x, y + 4),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(190, 192, 200),
            };
            container.Controls.Add(lblModel);

            _cmbControllerModel = new ComboBox
            {
                Location = new Point(x + 66, y),
                Width = 150,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f),
            };
            _cmbControllerModel.Items.Add("Oculus / Quest Touch");
            _cmbControllerModel.Items.Add("Valve Index Knuckles");
            _cmbControllerModel.SelectedIndex = 0;
            _cmbControllerModel.SelectedIndexChanged += (s, e) =>
            {
                ApplyControllerModel(_cmbControllerModel.SelectedIndex == 1 ? "knuckles" : "touch");
                SaveUiModelChoice();
            };
            container.Controls.Add(_cmbControllerModel);

            // Restore the persisted choice (fires the handler above when knuckles)
            if (LoadUiModelChoice() == "knuckles")
                _cmbControllerModel.SelectedIndex = 1;

            _chkMoveDots = new CheckBox
            {
                Text = "Move dots (drag to calibrate, saves on release)",
                Location = new Point(x + 228, y + 2),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(190, 192, 200),
            };
            container.Controls.Add(_chkMoveDots);
        }

        private void ApplyControllerModel(string key)
        {
            _controllerModelKey = key;
            var defaults = key == "knuckles" ? ControllerButtonsKnuckles : ControllerButtons;
            _activeControllerButtons = defaults.ToDictionary(kv => kv.Key, kv => kv.Value);
            ApplyDotOverrides(key);

            _selectedCtrlButton = null;
            _hoveredCtrlButton = null;
            _dragCtrlButton = null;
            if (_picBindingsController != null)
            {
                _picBindingsController.Image = ActiveControllerImage;
                _picBindingsController.Invalidate();
            }
            // Mirror the photo on the Settings tab controller picture too, and
            // force a repaint so the shortcut circles jump to the new layout
            // immediately (the Image swap alone was not refreshing the overlay).
            if (_picControllers != null)
            {
                _picControllers.Image = ActiveControllerImage;
                _picControllers.Invalidate();
                _picControllers.Update();
            }

            // The trackpad swipe shortcut only exists on Index knuckles
            bool knuckles = key == "knuckles";
            if (_lblTrackpadSwipe != null) _lblTrackpadSwipe.Visible = knuckles;
            if (_cmbTrackpadSwipe != null) _cmbTrackpadSwipe.Visible = knuckles;
            if (_lblTrackpadSwipeHint != null) _lblTrackpadSwipeHint.Visible = knuckles;

            // Gestures tab: hold-button options follow the controller model
            RefreshGestureHoldOptions();
        }

        // VR Keyboard Shortcut picture (Settings tab): model-aware positions
        // for the clickable stick/A/B/X/Y dots, fed from the same calibrated
        // layout the Bindings tab uses.
        private Dictionary<string, PointF[]> ShortcutButtonPositions()
        {
            if (_controllerModelKey != "knuckles")
                return ButtonPositions;

            var map = new Dictionary<string, PointF[]>();
            void Add(string shortcutKey, string bindingsKey)
            {
                if (_activeControllerButtons.TryGetValue(bindingsKey, out var entry))
                    map[shortcutKey] = new[] { entry.pos };
            }
            Add("left_stick", "left_stick");
            Add("x", "x_button");
            Add("y", "y_button");
            Add("right_stick", "right_stick");
            Add("a", "a_button");
            Add("b", "b_button");
            return map;
        }

        private float ShortcutDotRadius => _controllerModelKey == "knuckles" ? 7f : 10f;
        private float ShortcutHitRadius => _controllerModelKey == "knuckles" ? 0.03f : 0.06f;

        // Push the current model (photo + calibrated positions) into the combo
        // editor popup so the VR keyboard shortcut picker mirrors this tab.
        private void SyncComboEditorModel()
        {
            ComboEditForm.ControllerModelKey = _controllerModelKey;
            if (_controllerModelKey != "knuckles")
            {
                ComboEditForm.ModelPositionOverrides = null;
                return;
            }
            var map = new Dictionary<string, PointF>();
            foreach (var kv in _activeControllerButtons)
            {
                string comboKey = kv.Key switch
                {
                    "x_button" => "x",
                    "y_button" => "y",
                    "l_trigger" => "left_trigger",
                    "l_grip" => "left_grip",
                    "a_button" => "a",
                    "b_button" => "b",
                    "r_trigger" => "right_trigger",
                    "r_grip" => "right_grip",
                    _ => kv.Key, // sticks + directions share names; trackpads have no combo entry
                };
                map[comboKey] = kv.Value.pos;
            }
            ComboEditForm.ModelPositionOverrides = map;
        }

        // ── Dot layout persistence ──

        private void ApplyDotOverrides(string key)
        {
            try
            {
                if (!File.Exists(DotLayoutPath)) return;
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float[]>>>(
                    File.ReadAllText(DotLayoutPath));
                if (all == null || !all.TryGetValue(key, out var layout)) return;

                foreach (var kv in layout)
                {
                    if (kv.Value.Length < 2) continue;
                    if (_activeControllerButtons.TryGetValue(kv.Key, out var entry))
                        _activeControllerButtons[kv.Key] = (entry.display, new PointF(kv.Value[0], kv.Value[1]), entry.isStickDir);
                }
            }
            catch { /* corrupt layout file: fall back to defaults */ }
        }

        private void SaveDotOverrides()
        {
            try
            {
                Dictionary<string, Dictionary<string, float[]>> all = new();
                if (File.Exists(DotLayoutPath))
                {
                    try
                    {
                        all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float[]>>>(
                            File.ReadAllText(DotLayoutPath)) ?? new();
                    }
                    catch { all = new(); }
                }

                all[_controllerModelKey] = _activeControllerButtons.ToDictionary(
                    kv => kv.Key, kv => new[] { kv.Value.pos.X, kv.Value.pos.Y });

                File.WriteAllText(DotLayoutPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                _lblKbStatus.Text = $"Dot layout saved for {_controllerModelKey} ({Path.GetFileName(DotLayoutPath)})";
                _lblKbStatus.ForeColor = Color.FromArgb(100, 200, 100);
            }
            catch (Exception ex)
            {
                _lblKbStatus.Text = $"Dot layout save failed: {ex.Message}";
                _lblKbStatus.ForeColor = Color.FromArgb(255, 100, 100);
            }
        }

        // ── Drag handlers (wired from BuildKeyboardTab) ──

        private void PicBindingsController_MouseDown(object? sender, MouseEventArgs e)
        {
            if (!_chkMoveDots.Checked || e.Button != MouseButtons.Left) return;
            var (drawW, drawH, offX, offY) = GetBindingsImageBounds();
            float fx = (e.X - offX) / drawW;
            float fy = (e.Y - offY) / drawH;

            string? closest = null;
            float best = float.MaxValue;
            foreach (var kvp in _activeControllerButtons)
            {
                if (!IsControllerButtonVisible(kvp.Key)) continue;
                float dx = fx - kvp.Value.pos.X, dy = fy - kvp.Value.pos.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d < 0.05f && d < best) { best = d; closest = kvp.Key; }
            }
            _dragCtrlButton = closest;
        }

        private void PicBindingsController_MouseUp(object? sender, MouseEventArgs e)
        {
            if (_dragCtrlButton != null)
            {
                _dragCtrlButton = null;
                SaveDotOverrides();
            }
        }

        // Returns true when the move handled a drag (caller skips hover logic)
        private bool HandleDotDragMove(MouseEventArgs e)
        {
            if (_dragCtrlButton == null || !_chkMoveDots.Checked) return false;
            var (drawW, drawH, offX, offY) = GetBindingsImageBounds();
            float fx = Math.Clamp((e.X - offX) / drawW, 0f, 1f);
            float fy = Math.Clamp((e.Y - offY) / drawH, 0f, 1f);
            if (_activeControllerButtons.TryGetValue(_dragCtrlButton, out var entry))
            {
                _activeControllerButtons[_dragCtrlButton] = (entry.display, new PointF(fx, fy), entry.isStickDir);
                _picBindingsController.Invalidate();
            }
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BuzzGUI.Interfaces;
using Microsoft.Win32;

namespace PedalDrumGrid
{
    // Embedded GUI discovered via IMachineGUIFactory assembly scan (Core §26.1).
    // PreferWindowedGUI = false -> sits at the top of the parameter window.
    [MachineGUIFactoryDecl(PreferWindowedGUI = false)]
    public class PedalDrumGridGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalDrumGridGui();
    }

    // Composed of standard controls -> UserControl is correct (Core §26.7; switch
    // to FrameworkElement only if an OnRender-painted surface is added).
    public class PedalDrumGridGui : UserControl, IMachineGUI
    {
        IMachine _iMachine;
        PedalDrumGridMachine _machine;

        TextBlock _kitLabel;
        readonly TextBox[] _laneName = new TextBox[PedalDrumGridMachine.LANES];
        readonly ComboBox[] _laneCombo = new ComboBox[PedalDrumGridMachine.LANES];
        readonly ComboBox[] _laneOutCombo = new ComboBox[PedalDrumGridMachine.LANES];
        readonly TextBlock[] _laneStatus = new TextBlock[PedalDrumGridMachine.LANES];
        readonly TextBlock[] _laneFormat = new TextBlock[PedalDrumGridMachine.LANES];
        bool _populating;

        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _machine = value?.ManagedMachine as PedalDrumGridMachine;
                RefreshAll();
            }
        }

        public PedalDrumGridGui()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };

            _kitLabel = new TextBlock { Text = "Kit: (none)", Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.Bold };
            root.Children.Add(_kitLabel);

            // Kit + wavetable controls.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var loadBtn    = MakeButton("Load Kit…", LoadKit);
            var saveBtn    = MakeButton("Save Kit…", SaveKit);
            var refreshBtn = MakeButton("Refresh Waves", RefreshWaveLists);
            row.Children.Add(loadBtn);
            row.Children.Add(saveBtn);
            row.Children.Add(refreshBtn);
            root.Children.Add(row);

            // Per-lane wave assignment grid (scrollable: 16 rows).
            var lanesPanel = new StackPanel { Orientation = Orientation.Vertical };
            for (int lane = 0; lane < PedalDrumGridMachine.LANES; lane++)
            {
                int captured = lane;
                var laneRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

                laneRow.Children.Add(new TextBlock
                {
                    Text = "Lane " + (lane + 1),
                    Width = 56,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var nameBox = new TextBox { Width = 110, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                nameBox.TextChanged += (_, __) =>
                {
                    if (_populating || _machine == null) return;
                    _machine.Kit.SetLaneName(captured, nameBox.Text);
                };
                _laneName[lane] = nameBox;
                laneRow.Children.Add(nameBox);

                var combo = new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center };
                combo.SelectionChanged += (_, __) =>
                {
                    if (_populating || _machine == null) return;
                    if (combo.SelectedItem is WaveEntry we)
                    {
                        _machine.AssignLaneWave(captured, we.Index);
                        // AssignLaneWave captured the wave's name — reflect it.
                        _populating = true;
                        _laneName[captured].Text = _machine.Kit.GetLaneName(captured);
                        _populating = false;
                        _laneStatus[captured].Text = _machine.Kit.LaneDisplay(captured);
                        _laneFormat[captured].Text = _machine.Kit.LaneFormat(captured);
                    }
                };
                _laneCombo[lane] = combo;
                laneRow.Children.Add(combo);

                // Output bus picker — which multi-out this lane sums into.
                laneRow.Children.Add(new TextBlock
                {
                    Text = "\u2192",   // arrow
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var outCombo = new ComboBox { Width = 70, VerticalAlignment = VerticalAlignment.Center };
                outCombo.SelectionChanged += (_, __) =>
                {
                    if (_populating || _machine == null) return;
                    if (outCombo.SelectedIndex >= 0)
                        _machine.SetLaneOut(captured, outCombo.SelectedIndex + 1);
                };
                _laneOutCombo[lane] = outCombo;
                laneRow.Children.Add(outCombo);

                var status = new TextBlock
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    Opacity = 0.7,
                    Width = 150,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _laneStatus[lane] = status;
                laneRow.Children.Add(status);

                // Native sample-rate / bit-depth, to the right of the status.
                var fmt = new TextBlock
                {
                    Margin = new Thickness(10, 0, 0, 0),
                    Opacity = 0.6,
                    Width = 64,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _laneFormat[lane] = fmt;
                laneRow.Children.Add(fmt);

                lanesPanel.Children.Add(laneRow);
            }

            root.Children.Add(new ScrollViewer
            {
                Content = lanesPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxWidth = 560,   // keep the window compact; scroll right for the format column
                MaxHeight = 360
            });

            root.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                Opacity = 0.7,
                Width = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Text = "Assign a wavetable wave per lane here (or load a .pdrumgrid.xml kit). " +
                       "The \u2192 Out picker routes each lane to a multi-out bus — point several " +
                       "lanes at the same bus to group them (e.g. all hats \u2192 Out 3). Out 0 is the " +
                       "full mix. Triggers go in the pattern editor — one Trig column per lane, type 1 to hit."
            });

            Content = root;
        }

        Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 2, 6, 2) };
            b.Click += (_, __) => onClick();
            return b;
        }

        void LoadKit()
        {
            if (_machine == null) return;
            var dlg = new OpenFileDialog { Filter = "Pedal DrumGrid kit (*.pdrumgrid.xml)|*.pdrumgrid.xml|All files|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _machine.LoadKit(dlg.FileName);
                RefreshAll();
            }
        }

        void SaveKit()
        {
            if (_machine == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "Pedal DrumGrid kit (*.pdrumgrid.xml)|*.pdrumgrid.xml",
                FileName = (_machine.Kit.Name ?? "Kit") + ".pdrumgrid.xml"
            };
            if (dlg.ShowDialog() == true)
            {
                _machine.SaveKit(dlg.FileName);   // embeds audio + per-lane vel/pitch
                RefreshKitLabel();
            }
        }

        // Re-enumerate the wavetable and rebuild every lane's combo (waves may
        // have been added/renamed since the GUI opened).
        void RefreshWaveLists()
        {
            if (_machine == null) return;
            var entries = _machine.GetWavetableEntries();
            _populating = true;
            try
            {
                for (int lane = 0; lane < PedalDrumGridMachine.LANES; lane++)
                {
                    var combo = _laneCombo[lane];
                    combo.ItemsSource = null;
                    combo.ItemsSource = new List<WaveEntry>(entries);

                    int want = _machine.Kit.GetWaveIndex(lane);   // 0 if not a WT lane
                    int sel = 0;
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Index == want) { sel = i; break; }
                    combo.SelectedIndex = sel;

                    // Output bus list (Out 1..LANES), select this lane's routing.
                    var oc = _laneOutCombo[lane];
                    if (oc.Items.Count != PedalDrumGridMachine.LANES)
                    {
                        var buses = new List<string>(PedalDrumGridMachine.LANES);
                        for (int b = 1; b <= PedalDrumGridMachine.LANES; b++) buses.Add("Out " + b);
                        oc.ItemsSource = buses;
                    }
                    oc.SelectedIndex = _machine.GetLaneOut(lane) - 1;

                    _laneName[lane].Text = _machine.Kit.GetLaneName(lane);
                    _laneStatus[lane].Text = _machine.Kit.LaneDisplay(lane);
                    _laneFormat[lane].Text = _machine.Kit.LaneFormat(lane);
                }
            }
            finally { _populating = false; }
        }

        void RefreshKitLabel()
        {
            if (_machine == null) return;
            _kitLabel.Text = "Kit: " + (_machine.Kit.Name ?? "(none)");
        }

        void RefreshAll()
        {
            if (_machine == null) return;
            RefreshKitLabel();
            RefreshWaveLists();
        }
    }
}

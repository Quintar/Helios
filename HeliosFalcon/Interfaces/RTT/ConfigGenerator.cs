﻿// Copyright 2023 Helios Contributors
// 
// HeliosFalcon is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// HeliosFalcon is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Serialization;
using GadrocsWorkshop.Helios.ComponentModel;
using GadrocsWorkshop.Helios.Util;
using GadrocsWorkshop.Helios.Util.Shadow;
using GadrocsWorkshop.Helios.Windows;
using GadrocsWorkshop.Helios.Windows.ViewModel;
using NLog;

/*
 
Design:

Profile Developers:

- enable RTT check box
- verify config generated matches expected viewports
- follow Profile Users flow

Profile Users:

- on load
    - if RTT pre-enabled and if no magic cookie, will disable Rtt itself and go to special status
- on status report
    - if disabled due to no magic cookie on load, show special status asking to enable for consent
    - show configuration that will be generated
    - error if profile wants to start/stop RTT but global policy disallows it
- on ready check
    - make sure falcon interface is configured 
    - make sure file either does not exist or is helios owned
    - error if profile wants to start/stop RTT but global policy disallows it
- on profile start
    - refuse to overwrite non-Helios file
    - launch RTT process 
        if process control is permitted in global settings (so that profile can't attack a user)
        and if the config was successfully updated
- on profile stop
    - stop RTT process 
        if process control is permitted in global settings (so that profile can't attack a user)
        and if we started it this run
- interactive:
    - on enable, if file exists but not Helios, show dialog and backup file (Profile Editor)

*/

/// <summary>
/// RTT configuration generator for Falcon Interface
/// </summary>
namespace GadrocsWorkshop.Helios.Interfaces.Falcon.Interfaces.RTT
{
    [XmlRoot("RTT", Namespace = XML_NAMESPACE)]
    public class ConfigGenerator : HeliosXmlModel, IDisposable
    {
        private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// map from expected viewport name to the RTT ini display name
        /// </summary>
        private static readonly Dictionary<string, string> SupportedViewports = new Dictionary<string, string>
        {
            {"HUD", "HUD"},
            {"PFL", "PFL"},
            {"DED", "DED"},
            {"RWR", "RWR"},
            {"MFDLEFT", "MFDLEFT"},
            {"MFDRIGHT", "MFDRIGHT"},
            {"HMS", "HMS"},
            {"HUD_ONTOP", "HUD"},
            {"PFL_ONTOP", "PFL"},
            {"DED_ONTOP", "DED"},
            {"RWR_ONTOP", "RWR"},
            {"MFDLEFT_ONTOP", "MFDLEFT"},
            {"MFDRIGHT_ONTOP", "MFDRIGHT"},
            {"HMS_ONTOP", "HMS"}
        };

        // our schema identifier, in case of future configuration model changes
        public const string XML_NAMESPACE =
            "http://Helios.local/HeliosFalcon/Interfaces/RTT/RttConfigGenerator";

        private const int DEFAULT_RENDERER = 0;

        private string _rttFileHeader =
            $"### RTT Client Config, generated by Helios {RunningVersion.FromHeliosAssembly()} , DO NOT EDIT";

        /// <summary>
        /// the configuration we generated based on the most recent viewport positions
        /// </summary>
        private List<string> _lines = new List<string>();

        /// <summary>
        /// backing field for property Networked, contains
        /// true if RTT will run networked
        /// </summary>
        private bool _networked;

        /// <summary>
        /// backing field for property NetworkOptions, contains
        /// configuration options to use for networked mode
        /// </summary>
        private NetworkOptions _networkOptions = new NetworkOptions();

        /// <summary>
        /// backing field for property LocalOptions, contains
        /// configuration options to use for local mode
        /// </summary>
        private LocalOptions _localOptions = new LocalOptions();

        /// <summary>
        /// backing field for property Enabled, contains
        /// true if RTT functionality is enabled
        /// </summary>
        private bool _enabled;

        /// <summary>
        /// backing field for property Renderer, contains
        /// int value from 0-6 defining which type of renderer
        /// will be used.
        /// </summary>
        private int _renderer = DEFAULT_RENDERER;

        /// <summary>
        /// backing field for property EnabledCommand, contains
        /// handler for interaction with the visual representation (such as checkbox) of the Enabled property
        /// </summary>
        private ICommand _enabledCommand;

        /// <summary>
        /// backing field for property DisabledUntilConsent, contains
        /// true if the Profile enabled this feature but we had to turn it off until the user can consent to overwrite the config file
        /// </summary>
        private bool _disabledUntilConsent;

        /// <summary>
        /// backing field for property ProcessControl, contains
        /// process control configuration options
        /// </summary>
        private ProcessControl _processControl = new ProcessControl();

        public ConfigGenerator() : base(XML_NAMESPACE)
        {
            _localOptions.PropertyChanged += Child_PropertyChanged;
            _networkOptions.PropertyChanged += Child_PropertyChanged;
            _processControl.PropertyChanged += Child_PropertyChanged;
        }

        internal void Update(IEnumerable<ShadowVisual> viewports)
        {
            if (!Enabled)
            {
                return;
            }

            // update our generated configuration
            _lines = GenerateConfig(viewports);
        }

        /// <summary>
        /// called when added to the profile, to fix up the configuration before changes are observed
        /// </summary>
        internal void OnLoaded()
        {
            if (!Enabled)
            {
                return;
            }

            if (string.IsNullOrEmpty(Parent.FalconPath))
            {
                // need falcon location to check for consent
                DisableUntilConsent();
                return;
            }

            CalculatePaths(out string _, out string outputPath);
            if (File.Exists(outputPath))
            {
                // check for athorization to overwrite
                string existingContents = ReadFile(outputPath);
                if (!IsOwnedByHelios(existingContents))
                {
                    // need to wait for consent
                    DisableUntilConsent();
                }
            }
        }

        internal IEnumerable<StatusReportItem> OnStatusReport(IEnumerable<ShadowVisual> viewports)
        {
            if (DisabledUntilConsent)
            {
                return new StatusReportItem
                {
                    Severity = StatusReportItem.SeverityCode.Error,
                    Status =
                        "RTT feature has been disabled, because an existing RTT configuration would be overwritten by this profile",
                    Recommendation =
                        "Enable the RTT feature again to back up the existing file and let Helios generate RTT displays"
                }.AsReport();
            }

            if (!Enabled)
            {
                return new StatusReportItem[0];
            }

            // gather status from child objects, if any
            IEnumerable<StatusReportItem> processControlReport =
                ProcessControl?.OnStatusReport() ?? new StatusReportItem[0];

            return processControlReport
                .Concat(ReportLInes());
        }

        private IEnumerable<StatusReportItem> ReportLInes()
        {
            // we show the whole generated file, so it will be in the interface status report
            return _lines
                .Select(line => new StatusReportItem
                {
                    Severity = StatusReportItem.SeverityCode.Info,
                    Status = line,
                    Flags = StatusReportItem.StatusFlags.ConfigurationUpToDate | StatusReportItem.StatusFlags.Verbose
                });
        }

        /// <summary>
        /// called when the user interactively enables the RTT feature, either initially or because it was switched off
        /// due to consent being required
        /// </summary>
        /// <param name="source"></param>
        internal void OnInteractivelyEnabled(CheckBox source)
        {
            CalculatePaths(out string _, out string outputPath);
            if (!File.Exists(outputPath))
            {
                // we will create it when we start, no problem
                DisabledUntilConsent = false;
                return;
            }

            // check if third party file needs to be preserved
            string existingContents = ReadFile(outputPath);
            if (IsOwnedByHelios(existingContents))
            {
                // already under our control, no problem
                DisabledUntilConsent = false;
                return;
            }

            // determine what the backup file might be right now
            string backupPath = Path.ChangeExtension(outputPath, "original.txt");
            int n = 1;
            while (File.Exists(backupPath))
            {
                n++;
                backupPath = Path.ChangeExtension(outputPath, $"original{n}.txt");
            }

            // display a warning
            InstallationDangerPromptModel warningModel = new InstallationDangerPromptModel
            {
                Title = "Advanced Operation Requested",
                Message = "You are about to enable the Falcon RTT configuration feature of Helios.  Doing so will grant Helios permission to change this file when you start a profile.",
                Info = new List<StructuredInfo>
                {
                    new StructuredInfo
                    {
                        Message = $"A backup of your current RTT config file 'RTTClient.ini' will be stored at {backupPath} for you."
                    }
                }
            };
            Dialog.ShowModalCommand.Execute(new ShowModalParameter
            {
                Content = warningModel
            }, source);

            switch (warningModel.Result)
            {
                case InstallationPromptResult.Cancel:
                    {
                        // undo it
                        Enabled = false;
                        break;
                    }

                case InstallationPromptResult.Ok:
                    {
                        // take over, so from now on we own this file
                        File.Move(outputPath, backupPath);
                        WriteFile(outputPath, PrepareContents());
                        DisabledUntilConsent = false;
                        break;
                    }
            }
        }

        /// <summary>
        /// create a ready check items for our parent's ReadyCheck
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<StatusReportItem> OnReadyCheck()
        {
            if (DisabledUntilConsent)
            {
                yield return new StatusReportItem
                {
                    Severity = StatusReportItem.SeverityCode.Error,
                    Status = "RTT feature has been disabled, because an existing RTT configuration would be overwritten by this profile",
                    Link = StatusReportItem.ProfileEditor,
                    Recommendation = "Enable the RTT feature again to back up the existing file and allow Helios to generate RTT displays"
                };
                yield break;
            }

            if (!Enabled)
            {
                yield return new StatusReportItem
                {
                    Status = "RTT client configuration feature is not enabled",
                    Flags = StatusReportItem.StatusFlags.ConfigurationUpToDate | StatusReportItem.StatusFlags.Verbose
                };
                yield break;
            }

            yield return CheckConfigFile();

            if (ProcessControl != null)
            {
                foreach (StatusReportItem statusReportItem in ProcessControl.OnReadyCheck())
                {
                    yield return statusReportItem;
                }
            }
        }

        private StatusReportItem CheckConfigFile()
        {
            if (string.IsNullOrEmpty(Parent.FalconPath))
            {
                return new StatusReportItem
                {
                    Status = "Falcon Interface must be configured before RTT configuration can be generated",
                    Link = StatusReportItem.ProfileEditor,
                    Severity = StatusReportItem.SeverityCode.Error,
                    Recommendation = "Configure the Falcon interface"
                };
            }

            if (!Directory.Exists(Path.GetDirectoryName(Parent.FalconPath)))
            {
                return new StatusReportItem
                {
                    Status = $"Falcon directory not found at {Anonymizer.Anonymize(Parent.FalconPath)}",
                    Link = StatusReportItem.ProfileEditor,
                    Severity = StatusReportItem.SeverityCode.Error,
                    Recommendation = "Configure the Falcon version in the Falcon Interface"
                };
            }

            // construct file contents
            CalculatePaths(out string _, out string outputPath);

            if (File.Exists(outputPath))
            {
                string existingContents = ReadFile(outputPath);

                // check if the file is already identical
                string contents = PrepareContents();
                if (existingContents?.Equals(contents) ?? false)
                {
                    return new StatusReportItem
                    {
                        Status = $"RTT configuration at {Anonymizer.Anonymize(outputPath)} is up to date",
                        Severity = StatusReportItem.SeverityCode.Info,
                        Flags = StatusReportItem.StatusFlags.ConfigurationUpToDate
                    };
                }

                if (!IsOwnedByHelios(existingContents))
                {
                    // not allowed to overwrite third party files
                    return new StatusReportItem
                    {
                        Status = $"{Anonymizer.Anonymize(outputPath)} contains configuration not generated by Helios",
                        Link = StatusReportItem.ProfileEditor,
                        Severity = StatusReportItem.SeverityCode.Error,
                        Recommendation = "Enable the RTT functionality again to back up existing file and allow Helios to generate RTT displays"
                    };
                }
            }

            return new StatusReportItem
            {
                Status = $"RTT Configuration at {Anonymizer.Anonymize(outputPath)} will be updated at Start",
                Severity = StatusReportItem.SeverityCode.Info
            };
        }

        internal void OnProfileStart()
        {
            // check for Helios ownership of the RTT file and if it is ours, write the RTT configuration for the current profile,
            // which may not be the most recent one we configured in Profile Editor
            if (!UpdateRttConfigurationFile())
            {
                // don't run with the wrong config
                return;
            }

            ProcessControl?.OnProfileStart(Parent, Networked);
        }

        internal void OnProfileStop()
        {
            ProcessControl?.OnProfileStop(Parent, Networked);
        }

        /// <summary>
        /// for a viewport, looks up the RTT display that it references and returns that as a key or null if not found
        /// </summary>
        /// <param name="shadow"></param>
        /// <returns></returns>
        private static KeyValuePair<string, ShadowVisual> LookupDisplay(ShadowVisual shadow)
        {
            SupportedViewports.TryGetValue(shadow.Viewport.ViewportName, out string displayName);
            return new KeyValuePair<string, ShadowVisual>(displayName, shadow);
        }

        /// <summary>
        /// Read inputPath to string
        /// </summary>
        /// <param name="inputPath"></param>
        /// <returns></returns>
        private static string ReadFile(string inputPath)
        {
            using (StreamReader streamReader = new StreamReader(inputPath, new UTF8Encoding(false)))
            {
                return streamReader.ReadToEnd();
            }
        }

        /// <summary>
        /// Write text to outputPath
        /// </summary>
        /// <param name="outputPath"></param>
        /// <param name="text"></param>
        private static void WriteFile(string outputPath, string text)
        {
            using (StreamWriter streamWriter = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                streamWriter.Write(text);
            }
        }

        private bool UpdateRttConfigurationFile()
        {
            // called directly from Start, perhaps without a ready check, we need to check the magic cookie again
            CalculatePaths(out string outputDirectory, out string outputPath);

            // create contents we want
            string contents = PrepareContents();

            if (File.Exists(outputPath))
            {
                string existingContents = ReadFile(outputPath);

                if (existingContents == contents)
                {
                    // already identical, normal case in production
                    Logger.Info("RTT configuration at {Path} is up to date", Anonymizer.Anonymize(outputPath));
                    return true;
                }

                if (!IsOwnedByHelios(existingContents))
                {
                    // no UI available, just log and do nothing
                    Logger.Warn(
                        "RTT feature is not operable because existing RTT configuration file at {Path} may not be overwritten by Helios; please configure RTT feature in Profile Editor",
                        Anonymizer.Anonymize(outputPath));
                    return false;
                }
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
                WriteFile(outputPath, contents);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "failed to write RTT configuration file; check file permissions or whether file is in use by another application");
                throw;
            }
        }

        private void DisableUntilConsent()
        {
            Enabled = false;
            DisabledUntilConsent = true;
        }

        private List<string> GenerateConfig(IEnumerable<ShadowVisual> viewports)
        {
            // map of first occurrence of each supported display to its shadow visual for those that exist
            Dictionary<string, ShadowVisual> present = viewports
                .Select(LookupDisplay)
                .Where(kv => !string.IsNullOrEmpty(kv.Key))
                .Distinct(new KeyComparer())
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // unique list of supported displays
            HashSet<string> supportedDisplays = new HashSet<string>(SupportedViewports.Values);

            // now build the file contents
            List<string> lines = new List<string>
                {
                    $"{_rttFileHeader}"
                }
                .Concat(GenerateGlobalConfig())
                .Concat(supportedDisplays.SelectMany(candidate => EnableViewport(candidate, present)))
                .Concat(present.SelectMany(DefineViewport))
                .Concat(present.SelectMany(DefineViewportOnTop))
                .ToList();
            return lines;
        }

        private IEnumerable<string> GenerateGlobalConfig()
        {
            // generate configuration based on configured options
            yield return $"RENDERER = {Renderer}";
            yield return $"NETWORKED = {(Networked ? "1" : "0")}";
            yield return $"FPS = {LocalOptions.FramesPerSecond}";
            yield return $"HOST = {NetworkOptions.IPAddress}";
            yield return $"PORT = {NetworkOptions.Port}";
            yield return $"DATA_F4 = {(NetworkOptions.DataF4 ? "1" : "0")}";
            yield return $"DATA_BMS = {(NetworkOptions.DataBms ? "1" : "0")}";
            yield return $"DATA_OSB = {(NetworkOptions.DataOsb ? "1" : "0")}";
            yield return $"DATA_IVIBE = {(NetworkOptions.DataIvibe ? "1" : "0")}";
            yield return $"STRING_STR = {(NetworkOptions.StringStr ? "1" : "0")}";
            yield return $"STRING_DRW = {(NetworkOptions.StringDrw ? "1" : "0")}";
            yield return $"RWR_GRID = {(LocalOptions.RWRGrid ? "1" : "0")}";
        }

        private IEnumerable<string> EnableViewport(string candidateDisplay, Dictionary<string, ShadowVisual> present)
        {
            if (present.ContainsKey(candidateDisplay))
            {
                yield return $"USE_{candidateDisplay} = 1";
            }
            else
            {
                yield return $"USE_{candidateDisplay} = 0";
            }
        }

        private IEnumerable<string> DefineViewportOnTop(KeyValuePair<string, ShadowVisual> displayRecord)
        {
            if (displayRecord.Value.Viewport.ViewportName.Contains("ONTOP"))
            {
                yield return $"{displayRecord.Key}_ONTOP = 1";
            }
            else
            {
                yield return $"{displayRecord.Key}_ONTOP = 0";
            }
        }

        private IEnumerable<string> DefineViewport(KeyValuePair<string, ShadowVisual> displayRecord)
        {
            // names of settings in the RTT ini file are constructed from well-known display names
            string name = displayRecord.Key;

            // find global windows coordinate (which is same as Helios coordinate)
            Rect global = displayRecord.Value.Visual.CalculateWindowsDesktopRect();

            // emit rounded integer coordinates and sizes
            yield return $"{name}_X = {(int) (global.Left + 0.5)}";
            yield return $"{name}_Y = {(int) (global.Top + 0.5)}";
            yield return $"{name}_W = {(int) (global.Width + 0.5)}";
            yield return $"{name}_H = {(int) (global.Height + 0.5)}";
        }

        // this is duplicated work at OnReadyCheck and final OnProfileStart time, and is encapsulated here in case we decide to cache it later
        private string PrepareContents()
        {
            string contents = string.Join(Environment.NewLine, _lines);
            return contents;
        }

        private void CalculatePaths(out string outputDirectory, out string outputPath)
        {
            outputDirectory = Path.Combine(Parent.FalconPath, "Tools", "RTTRemote");
            outputPath = Path.Combine(outputDirectory, "RTTClient.ini");
        }

        /// <summary>
        /// check if file contents indicate that the file was written by Helios
        /// </summary>
        /// <param name="existingContents"></param>
        /// <returns></returns>
        private bool IsOwnedByHelios(string existingContents) =>
            existingContents.Contains(_rttFileHeader) ? true : false;

        #region Event Handlers

        private void Child_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // bubble this up without invalidating the entire LocalOptions or NetworkOptions
            OnPropertyChanged(
                new PropertyNotificationEventArgs(this, "ChildProperty", e as PropertyNotificationEventArgs));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            LocalOptions = null;
            NetworkOptions = null;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Type of renderer selected
        /// </summary>
        [DefaultValue(DEFAULT_RENDERER)]
        [XmlElement("Renderer")]
        public int Renderer
        {
            get => _renderer;
            set
            {
                if (_renderer == value)
                {
                    return;
                }

                int oldValue = _renderer;
                _renderer = value;
                OnPropertyChanged("Renderer", oldValue, value, true);
            }
        }

        /// <summary>
        /// true if RTT will run networked
        /// </summary>
        [XmlAttribute("Networked")]
        public bool Networked
        {
            get => _networked;
            set
            {
                if (_networked == value)
                {
                    return;
                }

                bool oldValue = _networked;
                _networked = value;
                OnPropertyChanged("Networked", oldValue, value, true);
            }
        }

        /// <summary>
        /// configuration options to use for networked mode
        /// </summary>
        [XmlElement("Network")]
        public NetworkOptions NetworkOptions
        {
            get => _networkOptions;
            set
            {
                if (_networkOptions != null && _networkOptions == value)
                {
                    return;
                }

                NetworkOptions oldValue = _networkOptions;
                _networkOptions = value;
                OnPropertyChanged("NetworkOptions", oldValue, value, true);

                // bubble up child property events only for the current object
                if (null != oldValue)
                {
                    oldValue.PropertyChanged -= Child_PropertyChanged;
                }

                if (null != value)
                {
                    value.PropertyChanged += Child_PropertyChanged;
                }
            }
        }

        /// <summary>
        /// configuration options to use for local mode
        /// </summary>
        [XmlElement("Local")]
        public LocalOptions LocalOptions
        {
            get => _localOptions;
            set
            {
                if (_localOptions != null && _localOptions == value)
                {
                    return;
                }

                LocalOptions oldValue = _localOptions;
                _localOptions = value;
                OnPropertyChanged("LocalOptions", oldValue, value, true);

                // bubble up child property events only for the current object
                if (null != oldValue)
                {
                    oldValue.PropertyChanged -= Child_PropertyChanged;
                }

                if (null != value)
                {
                    value.PropertyChanged += Child_PropertyChanged;
                }
            }
        }

        /// <summary>
        /// Reports if the viewports are being hidden by the parent monitor by a Fill Background
        /// </summary>
        internal IEnumerable<StatusReportItem> ReportViewportMasking(IEnumerable<ShadowVisual> viewports)
        {
            bool isMasked = false;

            foreach (ShadowVisual viewport in viewports)
            {
                if (viewport.IsViewport && !viewport.Viewport.ViewportName.Contains("ONTOP"))
                {
                    isMasked = viewport.Monitor.FillBackground;
                }
            }

            if (isMasked)
            {
                yield return new StatusReportItem
                {
                    Status = "One or more RTT viewports are masked by a monitor with a fill background. The result is you won't see the RTT viewports being rendered to the monitor.",
                    Link = StatusReportItem.ProfileEditor,
                    Severity = StatusReportItem.SeverityCode.Warning,
                    Recommendation = "Review your profile and ensure monitors do not have Fill Background enabled when a viewport is configured for that monitor"
                };
            }
        }

        /// <summary>
        /// true if RTT functionality is enabled
        /// </summary>
        [XmlAttribute("Enabled")]
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                bool oldValue = _enabled;
                _enabled = value;
                OnPropertyChanged("Enabled", oldValue, value, true);
            }
        }

        /// <summary>
        /// process control configuration options
        /// </summary>
        [XmlElement("ProcessControl")]
        public ProcessControl ProcessControl
        {
            get => _processControl;
            set
            {
                if (_processControl == value) return;
                ProcessControl oldValue = _processControl;
                _processControl = value;
                OnPropertyChanged(nameof(ProcessControl), oldValue, value, true);

                // bubble up child property events only for the current object
                if (null != oldValue)
                {
                    oldValue.PropertyChanged -= Child_PropertyChanged;
                }

                if (null != value)
                {
                    value.PropertyChanged += Child_PropertyChanged;
                }
            }
        }

        /// <summary>
        /// true if the Profile enabled this feature but we had to turn it off until the user can consent to overwrite the config
        /// file
        /// </summary>
        [DefaultValue(false)]
        [XmlAttribute("DisabledUntilConsent")]
        public bool DisabledUntilConsent
        {
            get => _disabledUntilConsent;
            set
            {
                if (_disabledUntilConsent == value)
                {
                    return;
                }

                bool oldValue = _disabledUntilConsent;
                _disabledUntilConsent = value;
                OnPropertyChanged(nameof(DisabledUntilConsent), oldValue, value, true);
            }
        }

        /// <summary>
        /// handler for interaction with the visual representation (such as checkbox) of the Enabled property
        /// </summary>
        public ICommand EnabledCommand
        {
            get
            {
                _enabledCommand = _enabledCommand ?? new RelayCommand(parameter =>
                {
                    CheckBox source = (CheckBox) parameter;
                    if (!source.IsChecked ?? false)
                    {
                        // nothing to do here
                        return;
                    }

                    OnInteractivelyEnabled(source);
                });
                return _enabledCommand;
            }
        }

        [XmlIgnore] 
        public IRttGeneratorHost Parent { get; internal set; }

        #endregion

        /// <summary>
        /// comparator to check if two key value pairs have the same key
        /// </summary>
        private class KeyComparer : IEqualityComparer<KeyValuePair<string, ShadowVisual>>
        {
            #region IEqualityComparer<KeyValuePair<string,ShadowVisual>>

            public bool Equals(KeyValuePair<string, ShadowVisual> x, KeyValuePair<string, ShadowVisual> y) =>
                x.Key != null && x.Key.Equals(y.Key);

            public int GetHashCode(KeyValuePair<string, ShadowVisual> obj) =>
                obj.Key?.GetHashCode() ?? "".GetHashCode();

            #endregion
        }
    }
}
﻿// Copyright 2021 Ammo Goettsch
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

using GadrocsWorkshop.Helios.ComponentModel;
using GadrocsWorkshop.Helios.Util.Shadow;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Serialization;

namespace GadrocsWorkshop.Helios.Interfaces.Falcon.Interfaces.RTT
{
    [XmlRoot("RTT", Namespace = XML_NAMESPACE)]
    public class ConfigGenerator : HeliosXmlModel, IDisposable
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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
            {"HMS", "HMS"}
        };

        // our schema identifier, in case of future configuration model changes
        public const string XML_NAMESPACE =
            "http://Helios.local/HeliosFalcon/Interfaces/RTT/RttConfigGenerator";

        private string _contents;

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

        public ConfigGenerator() : base(XML_NAMESPACE)
        {
            _localOptions.PropertyChanged += Child_PropertyChanged;
            _networkOptions.PropertyChanged += Child_PropertyChanged;
        }

        internal void Update(IEnumerable<ShadowVisual> viewports)
        {
            if (!Enabled)
            {
                return;
            }

            IEnumerable<string> lines = GenerateConfig(viewports);

            // write to file, if applicable
            WriteContents(lines);
        }

        internal IEnumerable<StatusReportItem> CreateStatusReport(IEnumerable<ShadowVisual> viewports)
        {
            if (!Enabled)
            {
                return new StatusReportItem[0];
            }

            // REVISIT: for now, we show the whole generated file
            return GenerateConfig(viewports)
                .Select(line => new StatusReportItem
                {
                    Severity = StatusReportItem.SeverityCode.Info,
                    Status = line,
                    Flags = StatusReportItem.StatusFlags.ConfigurationUpToDate | StatusReportItem.StatusFlags.Verbose
                });
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

        private IEnumerable<string> GenerateConfig(IEnumerable<ShadowVisual> viewports)
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
            IEnumerable<string> lines = new List<string>
                {
                    $"### RTT Client Config, generated by Helios {RunningVersion.FromHeliosAssembly()}"
                }
                .Concat(WriteGlobalConfig())
                .Concat(supportedDisplays.SelectMany(candidate => EnableViewport(candidate, present)))
                .Concat(present.SelectMany(DefineViewport));
            return lines;
        }

        private IEnumerable<string> WriteGlobalConfig()
        {
            // TODO: yield return each line of configuration based on configured options, remove yield break
            yield return $"RENDERER = {Renderer}";
            yield return $"NETWORKED = {(Networked ? "1" : "0")}";
            yield return $"FPS = {LocalOptions.FramesPerSecond}";
            yield return $"HOST = {NetworkOptions.IPAddress}";  //TODO loopback should be a default unless specified
            yield return $"PORT = {NetworkOptions.Port}"; //TODO port 44000 should be a default unless specified
            yield return $"DATA_F4 = {(NetworkOptions.DataF4 ? "1" : "0")}";
            yield return $"DATA_BMS = {(NetworkOptions.DataBms ? "1" : "0")}";
            yield return $"DATA_Osb = {(NetworkOptions.DataOsb ? "1" : "0")}";
            yield return $"DATA_IVIBE = {(NetworkOptions.DataIvibe ? "1" : "0")}";
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

        private IEnumerable<string> DefineViewport(KeyValuePair<string, ShadowVisual> displayRecord)
        {
            string name = displayRecord.Key;
            ShadowVisual shadowVisual = displayRecord.Value;

            // find global windows coordinate (which is same as Helios coordinate)
            double X = 0.5;
            double Y = 0.5;
            int depth = 0;
            HeliosVisual trace = shadowVisual.Visual;

            // arbitrary depth limit on loop in case of broken structures that could cause infinite loop otherwise
            while (trace != null && depth < 1024)
            {
                // XXX this won't work right with rotated panels, but that code is somewhere else? 
                X += trace.Left;
                Y += trace.Top;
                trace = trace.Parent;
                depth++;
            }

            // emit rounded integer coordinate
            yield return $"{name}_X = {(int) X}";
            yield return $"{name}_Y = {(int) Y}";

            // width and height are untransformed at this point
            yield return $"{name}_W = {(int) (0.5d + shadowVisual.Visual.Width)}";
            yield return $"{name}_H = {(int) (0.5d + shadowVisual.Visual.Height)}";
        }

        private void WriteContents(IEnumerable<string> lines)
        {
            string contents = string.Join(Environment.NewLine, lines);
            if (contents.Equals(_contents))
            {
                Logger.Debug("not writing unchanged RTT configuration to disk");
                return;
            }

            _contents = contents;
            // XXX write file
            FalconInterface falconInterface = new FalconInterface();
            string _rttClientIni = System.IO.Path.Combine(falconInterface.FalconPath, "Tools", "RTTRemote", "RTTClient.INI");
            if(System.IO.File.Exists(_rttClientIni))
            {
                System.IO.File.WriteAllText(_rttClientIni, contents);
            }
        }

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
            // TODO
        }

        #endregion

        #region Properties

        /// <summary>
        /// Type of renderer selected
        /// </summary>
        [XmlAttribute("Renderer")]
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
        /// backing field for property Enabled, contains
        /// true if RTT functionality is enabled
        /// </summary>
        private bool _enabled;

        /// <summary>
        /// backing field for property Renderer, contains
        /// int value from 0-6 defining which type of renderer
        /// will be used.
        /// </summary>
        private int _renderer;

        /// <summary>
        /// true if RTT functionality is enabled
        /// </summary>
        [XmlAttribute("Enabled")]
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                bool oldValue = _enabled;
                _enabled = value;
                OnPropertyChanged("Enabled", oldValue, value, true);
            }
        }

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
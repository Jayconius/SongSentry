// Fake OBS backend shared by LogicTest and Screens.
using System;
using System.Collections.Generic;
using System.Linq;

namespace SongSentry
{
    public sealed class FakeObs : IObsBackend
    {
        public bool Connected { get { return true; } }
        public readonly Dictionary<string, InputInfo> In = new Dictionary<string, InputInfo>();
        public readonly List<string> Calls = new List<string>();
        public Action<string, Dictionary<string, object>> Echo;   // simulate OBS raising events for our own changes

        public string Window = "Spotify Premium:Chrome_WidgetWin_1:Spotify.exe";

        public void Add(string name, string kind, bool muted, double vol, params bool[] tracks)
        {
            var i = new InputInfo { Name = name, Kind = kind, Muted = muted, VolumeMul = vol, HasAudio = true };
            for (int t = 0; t < 6; t++) i.Tracks[(t + 1).ToString()] = t < tracks.Length ? tracks[t] : false;
            In[name] = i;
        }

        public Dictionary<string, object> Request(string type, params object[] kv)
        {
            var a = Json.Make(kv);
            string n = Json.Str(a, "inputName");
            InputInfo i = n != null && In.ContainsKey(n) ? In[n] : null;
            switch (type)
            {
                case "GetInputList":
                    return Json.Make("inputs", new System.Collections.ArrayList(In.Values.Select(x => (object)Json.Make("inputName", x.Name, "inputKind", x.Kind)).ToList()));
                case "GetInputSettings":
                    return Json.Make("inputSettings", i.Kind == "wasapi_process_output_capture" ? Json.Make("window", Window)
                        : i.DeviceId != null ? Json.Make("device_id", i.DeviceId) : Json.Make());
                case "GetInputMute": return Json.Make("inputMuted", i.Muted);
                case "GetInputVolume": return Json.Make("inputVolumeMul", i.VolumeMul);
                case "GetInputAudioTracks": return Json.Make("inputAudioTracks", i.Tracks.ToDictionary(t => t.Key, t => (object)t.Value));
                case "GetProfileParameter":
                    string p = Json.Str(a, "parameterName");
                    return Json.Make("parameterValue", p == "Mode" ? "Advanced" : p == "TrackIndex" ? "1" : p == "VodTrackEnabled" ? "true" : p == "VodTrackIndex" ? "2" : null);
                case "GetStreamStatus": return Json.Make("outputActive", false);
                case "SetInputMute":
                    i.Muted = (bool)a["inputMuted"];
                    Calls.Add("mute " + n + " " + i.Muted);
                    if (Echo != null) Echo("InputMuteStateChanged", Json.Make("inputName", n, "inputMuted", i.Muted));
                    break;
                case "SetInputVolume":
                    i.VolumeMul = Convert.ToDouble(a["inputVolumeMul"]);
                    Calls.Add("volume " + n + " " + i.VolumeMul.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "SetInputAudioTracks":
                    var t2 = (Dictionary<string, object>)a["inputAudioTracks"];
                    foreach (var kv2 in t2) i.Tracks[kv2.Key] = (bool)kv2.Value;
                    Calls.Add("tracks " + n + " " + string.Join(",", t2.OrderBy(x => x.Key).Select(x => x.Key + "=" + ((bool)x.Value ? "on" : "off"))));
                    break;
            }
            return new Dictionary<string, object>();
        }
    }
}

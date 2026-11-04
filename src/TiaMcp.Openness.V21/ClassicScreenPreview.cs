using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    // Approximate supplied-document layout only. No project script, faceplate,
    // PLC connection or runtime action is executed by this artifact.
    internal static class ClassicScreenPreview
    {
        internal static JObject Review(XDocument document, IEnumerable<string> projectCultures)
        {
            var root = document.Descendants("Hmi.Screen.Screen").Single();
            var selected = ClassicScreenPreflight.Items(document).Take(513).ToArray();
            var items = selected.Take(512).ToArray();
            var warnings = new JArray();
            if (selected.Length > 512) warnings.Add(Warning("PreviewItemLimit", null, null));
            var cultures = projectCultures.Concat(document.Descendants("MultilingualTextItem").Select(m => ClassicScreenPreflight.Attribute(m, "Culture")))
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();
            var rows = new JArray();
            foreach (var item in items)
            {
                string name = ClassicScreenPreflight.Attribute(item, "ObjectName");
                int w = ClassicScreenPreflight.Number(item, "Width"), h = ClassicScreenPreflight.Number(item, "Height");
                var captions = new JObject();
                foreach (string culture in cultures)
                {
                    var text = item.Descendants("MultilingualTextItem").Where(m => ClassicScreenPreflight.Attribute(m, "Culture") == culture)
                        .Select(m => m.Element("AttributeList")?.Element("Text")?.Value).Where(t => !string.IsNullOrEmpty(t)).ToArray();
                    captions[culture] = string.Join(" / ", text);
                    if (text.Length == 0 && item.Descendants("MultilingualTextItem").Any()) warnings.Add(Warning("MissingTranslation", name, culture));
                    double font = Font(item);
                    if (text.Any(t => t.Split('\n').Any(line => line.Length * font * 0.55 > w - 8))) warnings.Add(Warning("PossibleTextOverflow", name, culture));
                }
                bool interactive = item.Descendants("Hmi.Event.Event").Any() || item.Name.LocalName.Contains("Faceplate")
                    || (ClassicScreenPreflight.Attribute(item, "Mode") ?? "").IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0;
                bool permission = item.Descendants().Any(x => x.Name.LocalName.IndexOf("Operability", StringComparison.OrdinalIgnoreCase) >= 0
                    || (ClassicScreenPreflight.Attribute(x, "Name") ?? "").IndexOf("Operability", StringComparison.OrdinalIgnoreCase) >= 0
                    || x.Name.LocalName.IndexOf("Authorization", StringComparison.OrdinalIgnoreCase) >= 0);
                if (interactive && !permission) warnings.Add(Warning("ReviewOperatingPermission", name, null));
                var visibility = item.Descendants("Hmi.Dynamic.VisibilityAnimation").Select(v => new JObject {
                    ["tag"] = v.Descendants("Tag").Elements("Name").Count() == 1 ? v.Descendants("Tag").Elements("Name").First().Value : null,
                    ["from"] = ClassicScreenPreflight.Attribute(v, "RangeStart"), ["to"] = ClassicScreenPreflight.Attribute(v, "RangeEnd") }).ToArray();
                rows.Add(new JObject { ["name"] = name, ["type"] = item.Name.LocalName, ["left"] = ClassicScreenPreflight.Number(item,"Left"),
                    ["top"] = ClassicScreenPreflight.Number(item,"Top"), ["width"] = w, ["height"] = h, ["font"] = Font(item),
                    ["captions"] = captions, ["visibility"] = new JArray(visibility), ["interactive"] = interactive, ["permissionPresent"] = permission,
                    ["tags"] = new JArray(item.Descendants("Tag").Elements("Name").Select(x => x.Value).Distinct()),
                    ["backColor"] = Color(ClassicScreenPreflight.Attribute(item,"BackColor")), ["foreColor"] = Color(ClassicScreenPreflight.Attribute(item,"ForeColor")) });
            }
            for (int i=0;i<rows.Count;i++) for(int j=i+1;j<rows.Count;j++)
            {
                var a=rows[i];var b=rows[j];
                int left=Math.Max(a.Value<int>("left"),b.Value<int>("left")), top=Math.Max(a.Value<int>("top"),b.Value<int>("top"));
                int right=Math.Min(a.Value<int>("left")+a.Value<int>("width"),b.Value<int>("left")+b.Value<int>("width"));
                int bottom=Math.Min(a.Value<int>("top")+a.Value<int>("height"),b.Value<int>("top")+b.Value<int>("height"));
                long overlap=(long)Math.Max(0,right-left)*Math.Max(0,bottom-top);
                if(overlap==0 || Exclusive(a,b)) continue;
                bool panel = a.Value<string>("type").EndsWith("Rectangle",StringComparison.Ordinal) || b.Value<string>("type").EndsWith("Rectangle",StringComparison.Ordinal);
                bool containment = overlap==(long)a.Value<int>("width")*a.Value<int>("height") || overlap==(long)b.Value<int>("width")*b.Value<int>("height");
                if(panel && containment) continue;
                warnings.Add(new JObject { ["code"]="ReviewOverlap", ["item"]=a["name"], ["otherItem"]=b["name"] });
            }
            return new JObject { ["screen"] = ClassicScreenPreflight.Attribute(root,"Name"), ["width"] = ClassicScreenPreflight.Number(root,"Width"),
                ["height"] = ClassicScreenPreflight.Number(root,"Height"), ["cultures"] = new JArray(cultures), ["items"] = rows, ["warnings"] = warnings,
                ["complete"] = selected.Length <= 512, ["runtimeVerified"] = false, ["rendering"] = "Approximate geometry/captions; faceplate internals, shared templates, fonts and graphics are not native runtime rendering." };
        }
        private static JObject Warning(string code,string item,string culture) => new JObject { ["code"]=code,["item"]=item,["culture"]=culture };
        private static double Font(XElement item) => double.TryParse(item.Descendants("Hmi.Globalization.FontItem").Select(x => ClassicScreenPreflight.Attribute(x,"FontSize")).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture,out double v) && v>=1 && v<=200 ? v : 12;
        private static string Color(string text)
        {
            var parts=(text??"").Split(',');
            return parts.Length==3 && parts.All(p=>byte.TryParse(p.Trim(),out _)) ? "rgb("+string.Join(",",parts.Select(p=>p.Trim()))+")" : null;
        }
        private static bool Exclusive(JToken a,JToken b)
        {
            foreach(var x in a["visibility"]) foreach(var y in b["visibility"])
                if(x.Value<string>("tag")!=null && x.Value<string>("tag")==y.Value<string>("tag")
                    && long.TryParse(x.Value<string>("from"),out long af) && long.TryParse(x.Value<string>("to"),out long at)
                    && long.TryParse(y.Value<string>("from"),out long bf) && long.TryParse(y.Value<string>("to"),out long bt) && (at<bf || bt<af)) return true;
            return false;
        }
        internal static string Html(JObject report)
        {
            string data=Convert.ToBase64String(Encoding.UTF8.GetBytes(report.ToString(Formatting.None)));
            return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'none'; connect-src 'none'\"><title>HMI layout preview</title><style>body{font:14px Arial;background:#eef1f4;color:#1b2a38}#canvas{position:relative;background:#f2f5f8;border:1px solid #999;margin-top:12px}.item{position:absolute;box-sizing:border-box;padding:3px;overflow:hidden;white-space:pre-wrap;border:1px solid #ccc}.conditional{border-style:dashed}button,input,select{margin:4px}#values label{display:inline-block;margin-right:12px}</style></head><body><h1>"+WebUtility.HtmlEncode(report.Value<string>("screen"))+"</h1><p>Approximate layout preview. Runtime, project scripts and machine controls are not executed. Dashed outlines have visibility conditions.</p><label>Language <select id='culture'></select></label><div id='values'></div><div id='canvas'></div><h2>Review findings</h2><pre id='findings'></pre><script>const d=JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('"+data+"'),c=>c.charCodeAt(0))));const c=document.getElementById('canvas'),l=document.getElementById('culture'),v=document.getElementById('values');c.style.width=d.width+'px';c.style.height=d.height+'px';for(const culture of d.cultures){const o=document.createElement('option');o.textContent=culture;l.append(o)}const inputs={};for(const tag of [...new Set(d.items.flatMap(i=>i.visibility.map(x=>x.tag)).filter(Boolean))]){const label=document.createElement('label');label.textContent=tag;const input=document.createElement('input');input.type='number';input.value='1';input.oninput=render;label.append(input);v.append(label);inputs[tag]=input}function render(){c.replaceChildren();for(const i of d.items){const e=document.createElement('div');e.className='item'+(i.visibility.length?' conditional':'');e.title=i.name+'\n'+i.tags.join('\n');for(const p of ['left','top','width','height'])e.style[p]=i[p]+'px';e.style.fontSize=i.font+'px';if(i.backColor)e.style.background=i.backColor;if(i.foreColor)e.style.color=i.foreColor;e.textContent=i.captions[l.value]||('['+i.type.split('.').pop()+']');if(i.visibility.some(x=>inputs[x.tag]&&(Number(inputs[x.tag].value)<Number(x.from)||Number(inputs[x.tag].value)>Number(x.to))))e.style.display='none';c.append(e)}}l.onchange=render;document.getElementById('findings').textContent=JSON.stringify(d.warnings,null,2);render();</script></body></html>";
        }
    }
}

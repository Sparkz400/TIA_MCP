using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Drive object handles. Startdrive cannot read DriveObject.DriveObjectNumber on some drives (a G120C PN V4.7 answers
    // "Drive object number could not be retrieved"), so a drive whose numbers are not all readable addresses every object by
    // its zero-based position in DriveObjectContainer.DriveObjects instead; one drive never mixes the two.
    internal static class DriveObjectRules
    {
        internal const string Number = "driveObjectNumber", Index = "driveObjectIndex";

        /// <summary>True when any number is unreadable (null): then every object of the drive is addressed by index.</summary>
        internal static bool ByIndex(IReadOnlyList<int?> numbers) => numbers.Any(number => number == null);

        internal static string DriveId(bool byIndex, int? number, int index) => byIndex ? "DO@" + index : "DO" + number;

        /// <summary>A JSON null for a null string. Newtonsoft turns a string-typed null into a String JValue, which prints as null but never
        /// DeepEquals the null of an echoed expectedTelegrams, so drive_telegram_set could not prepare (drive run dvn).</summary>
        internal static JToken Text(string value) => value == null ? JValue.CreateNull() : new JValue(value);

        /// <summary>The one handle a change payload names, driveObjectNumber or driveObjectIndex, after checking its value.</summary>
        internal static string Handle(JObject payload)
        {
            bool number = payload[Number] != null, index = payload[Index] != null;
            if (number == index) throw new BridgeException("InvalidRequest", "Name exactly one of driveObjectNumber or driveObjectIndex, whichever drive_observe reports.");
            var token = payload[number ? Number : Index];
            if (number && token.Type != JTokenType.Integer) throw new BridgeException("InvalidRequest", "driveObjectNumber must be the integer reported by drive_observe.");
            if (!number && (token.Type != JTokenType.Integer || token.Value<long>() < 0 || token.Value<long>() > int.MaxValue))
                throw new BridgeException("InvalidRequest", "driveObjectIndex must be the nonnegative integer reported by drive_observe.");
            return number ? Number : Index;
        }

        /// <summary>The container position of the object the payload names, given every object's number in container order (null when unreadable).</summary>
        internal static int Resolve(JObject payload, IReadOnlyList<int?> numbers)
        {
            string handle = Handle(payload);
            long value = payload.Value<long>(handle);
            bool byIndex = ByIndex(numbers);
            if (byIndex != (handle == Index))
                throw new BridgeException("DriveObjectHandleMismatch", byIndex
                    ? "A drive object number on the selected hardware item cannot be read, so drive_observe addresses its drive objects by driveObjectIndex; use driveObjectIndex."
                    : "Every drive object number on the selected hardware item can be read, so drive_observe addresses its drive objects by driveObjectNumber; use driveObjectNumber.");
            if (byIndex)
                return value < numbers.Count ? (int)value : throw new BridgeException("DriveObjectNotFound", "No drive object on the selected hardware item has driveObjectIndex " + value + ".");
            int found = -1;
            for (int i = 0; i < numbers.Count; i++)
            {
                if (numbers[i] != value) continue;
                if (found >= 0) throw new BridgeException("AmbiguousTarget", "More than one drive object has driveObjectNumber " + value + ".");
                found = i;
            }
            return found >= 0 ? found : throw new BridgeException("DriveObjectNotFound", "No drive object on the selected hardware item has driveObjectNumber " + value + ".");
        }

        /// <summary>What must not change between prepare and apply for the resolved object; it joins the change fingerprint.</summary>
        internal static JObject Identity(string hardwarePath, IReadOnlyList<int?> numbers, int index, string orderNumber, string firmware, JToken parameterCount)
            => new JObject { ["hardwarePath"] = hardwarePath, ["driveId"] = DriveId(ByIndex(numbers), numbers[index], index), ["driveObjectCount"] = numbers.Count,
                ["orderNumber"] = orderNumber, ["firmware"] = firmware, ["parameterCount"] = parameterCount };
    }
}

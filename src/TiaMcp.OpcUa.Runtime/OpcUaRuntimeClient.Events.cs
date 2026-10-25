using System.Globalization;
using Opc.Ua;

namespace TiaMcp.OpcUa.Runtime;

internal sealed partial class OpcUaRuntimeClient
{
    private static EventFilter EventFilter(OpcUaEventSelection selection)
    {
        static SimpleAttributeOperand Field(string name)=>new(){TypeDefinitionId=ObjectTypeIds.BaseEventType,
            AttributeId=Attributes.Value,BrowsePath=[new QualifiedName(name,0)]};
        return new EventFilter{SelectClauses=new(OpcUaEventContracts.Fields.Select(Field)),WhereClause=new ContentFilter{
            Elements=[new ContentFilterElement{FilterOperator=FilterOperator.GreaterThanOrEqual,
                FilterOperands=[new ExtensionObject(Field("Severity")),new ExtensionObject(new LiteralOperand{Value=new Variant(selection.MinimumSeverity)})]}]}};
    }
    private OpcUaEventField DecodeEventField(Variant field,int index)
    {
        string name=OpcUaEventContracts.Fields[index];
        OpcUaEventField result;
        if(field.Value is null)result=new(name,"null",null,null,null,null);
        else if(field.Value is StatusCode status)result=new(name,"status",null,null,null,$"0x{status.Code:x8}");
        else result=(index,field.Value) switch {
            (0,byte[] bytes)=>new(name,"value",Convert.ToBase64String(bytes),null,null,null),
            (1 or 2,NodeId node)=>new(name,"value",null,External(node)??throw Error("UaEventNamespaceUnavailable"),null,null),
            (3,string text)=>new(name,"value",text,null,null,null),
            (4 or 5,DateTime date)=>new(name,"value",DateTime.SpecifyKind(date,DateTimeKind.Utc).ToString("O"),null,null,null),
            (6,LocalizedText text)=>new(name,"value",text.Text,null,text.Locale,null),
            (7,ushort severity)=>new(name,"value",severity.ToString(CultureInfo.InvariantCulture),null,null,null),
            _=>throw Error("UaEventFieldTypeInvalid") };
        OpcUaEventContracts.ValidateField(result,index);return result;
    }
}

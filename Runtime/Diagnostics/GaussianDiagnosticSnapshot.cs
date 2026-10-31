using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace GaussianSplatLOD.Diagnostics
{
    // Diagnostics are optional: no diagnostic reflection is performed by the streaming hot path.
    internal sealed class GaussianMetricReader
    {
        readonly Dictionary<string,MemberInfo> members=new Dictionary<string,MemberInfo>();
        PropertyInfo metricsProperty;FieldInfo metricsField;Type metricsType;
        object metrics;
        public void Refresh(GaussianLODRenderer model)
        {
            if(metricsProperty==null&&metricsField==null){metricsProperty=typeof(GaussianLODRenderer).GetProperty("Metrics");metricsField=typeof(GaussianLODRenderer).GetField("Metrics");}
            metrics=metricsProperty!=null?metricsProperty.GetValue(model):metricsField!=null?metricsField.GetValue(model):null;
            if(metrics!=null&&metrics.GetType()!=metricsType){metricsType=metrics.GetType();members.Clear();}
        }
        object Value(string name)
        {
            if(metrics==null)return null;
            if(!members.TryGetValue(name,out var member)){member=(MemberInfo)metricsType.GetField(name)??metricsType.GetProperty(name);members[name]=member;}
            return member is FieldInfo f?f.GetValue(metrics):member is PropertyInfo p?p.GetValue(metrics):null;
        }
        public double Number(string name,double fallback=-1){object value=Value(name);return value==null?fallback:Convert.ToDouble(value,CultureInfo.InvariantCulture);}
        public string Text(string name,string fallback="Unavailable")=>Value(name)?.ToString()??fallback;
    }
}

using System;

namespace SmartClassNight
{
    /// <summary>
    /// 班级 id 读取器：天气页会把当前班级写入 localStorage（key: selectedClassId_weather），
    /// 主页天气 WebView 与“更多功能”WebView 同源（f18.llt-service.cn），共享同一份 localStorage。
    /// </summary>
    internal static class ClassIdReader
    {
        /// <summary>在 WebView 中执行的脚本：返回班级 id 字符串（找不到返回 null）。</summary>
        public const string Script = @"
            (function(){
                try{
                    function extract(v){
                        if(v==null) return null;
                        var s=String(v);
                        var m=s.match(/(\d{1,10})/);
                        return m?m[1]:null;
                    }
                    var direct=['selectedClassId_weather','classId','class_id','classID','class','selectedClass','selectedClassId','currentClass','userClass'];
                    for(var i=0;i<direct.length;i++){
                        var v=localStorage.getItem(direct[i]);
                        if(v!=null){ var r=extract(v); if(r) return r; }
                    }
                    for(var i=0;i<localStorage.length;i++){
                        var k=localStorage.key(i);
                        if(/class/i.test(k)){ var r=extract(localStorage.getItem(k)); if(r) return r; }
                    }
                    for(var i=0;i<sessionStorage.length;i++){
                        var k=sessionStorage.key(i);
                        if(/class/i.test(k)){ var r=extract(sessionStorage.getItem(k)); if(r) return r; }
                    }
                }catch(e){}
                return null;
            })()";

        /// <summary>解析 ExecuteScriptAsync 的返回值（可能带引号）。</summary>
        public static int? Parse(string? result)
        {
            if (string.IsNullOrEmpty(result) || result == "null") return null;
            result = result.Trim();
            if (result.Length >= 2 && result[0] == '"' && result[^1] == '"')
                result = result.Substring(1, result.Length - 2);
            return int.TryParse(result, out int id) && id > 0 ? id : null;
        }
    }
}

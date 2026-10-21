using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UsageWidget
{
    // ADE-only vectors. Never alter the widget's shared service drawings or theme.
    internal static class StudioVisuals
    {
        internal static FrameworkElement Icon(string name,Brush ink,double size=16)
        {
            string path;
            switch(name){
                case "folder":path="M3,7 L3,18 Q3,20 5,20 L19,20 Q21,20 21,18 L21,9 Q21,7 19,7 L12,7 L10,5 L5,5 Q3,5 3,7";break;
                case "search":path="M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M16,16 L21,21";break;
                case "new":path="M12,4 L6,4 Q4,4 4,6 L4,18 Q4,20 6,20 L18,20 Q20,20 20,18 L20,12 M10,14 L11,10 L19,2 L22,5 L14,13 Z";break;
                case "panel":path="M5,5 L19,5 Q21,5 21,7 L21,17 Q21,19 19,19 L5,19 Q3,19 3,17 L3,7 Q3,5 5,5 M9,5 L9,19";break;
                case "details":path="M5,5 L19,5 Q21,5 21,7 L21,17 Q21,19 19,19 L5,19 Q3,19 3,17 L3,7 Q3,5 5,5 M15,5 L15,19";break;
                case "transfer":path="M3,6 L7,6 C12,6 12,18 17,18 L21,18 M17,14 L21,18 L17,22 M3,18 L7,18 M14,6 L21,6 M17,2 L21,6 L17,10";break;
                case "lock":path="M6,10 L18,10 L18,20 L6,20 Z M8,10 L8,7 A4,4 0 0 1 16,7 L16,10 M12,14 L12,16";break;
                case "shield":path="M12,3 L20,6 L20,12 C20,17 12,21 12,21 C12,21 4,17 4,12 L4,6 Z M8,12 L11,15 L16,9";break;
                case "copy":path="M8,8 L20,8 L20,20 L8,20 Z M16,8 L16,4 L4,4 L4,16 L8,16";break;
                case "archive":path="M3,4 L21,4 L21,8 L3,8 Z M5,8 L5,20 L19,20 L19,8 M9,12 L15,12";break;
                case "settings":path="M10,3 L9,6 L6,7 L4,10 L6,12 L6,15 L8,18 L11,17 L14,18 L17,16 L17,13 L19,11 L18,7 L15,6 L13,3 Z M15,11 A3,3 0 1 1 9,11 A3,3 0 1 1 15,11";break;
                case "link":path="M10,8 L13,5 A4,4 0 0 1 19,11 L16,14 M14,16 L11,19 A4,4 0 0 1 5,13 L8,10 M8,16 L16,8";break;
                case "spark":path="M12,3 L14,10 L21,12 L14,14 L12,21 L10,14 L3,12 L10,10 Z";break;
                case "down":path="M6,9 L12,15 L18,9";break;
                case "close":path="M6,6 L18,18 M18,6 L6,18";break;
                case "plus":path="M12,5 L12,19 M5,12 L19,12";break;
                case "more":path="M5,11 L5,13 M12,11 L12,13 M19,11 L19,13";break;
                case "arrow":path="M12,20 L12,4 M6,10 L12,4 L18,10";break;
                case "refresh":path="M20,5 L20,11 L14,11 M4,19 L4,13 L10,13 M5,8 A8,8 0 0 1 19,6 M19,16 A8,8 0 0 1 5,18";break;
                default:path="M5,12 L19,12";break;
            }
            return new Viewbox{Width=size,Height=size,Stretch=Stretch.Uniform,Child=new Path{Data=Geometry.Parse(path),Stroke=ink,StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=24,Height=24,Stretch=Stretch.None},IsHitTestVisible=false};
        }
        internal static FrameworkElement Provider(string provider,Brush ink,double size=16)
        {
            var source=ServiceIcons.Get(provider!=null&&provider.StartsWith("gemini",StringComparison.Ordinal)?"gemini":provider);
            if(source==null)return Icon("spark",ink,size);
            return new Rectangle{Width=size,Height=size,Fill=ink,OpacityMask=new ImageBrush(source){Stretch=Stretch.Uniform},IsHitTestVisible=false};
        }
    }
}

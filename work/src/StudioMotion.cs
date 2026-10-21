using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UsageWidget
{
    // ADE-owned animation clocks only. Semantic state/input is never delayed by a transition.
    internal sealed class StudioMotion : IDisposable
    {
        readonly Func<bool> preference;
        readonly List<WeakReference> tracked=new List<WeakReference>();
        readonly List<WeakReference> columns=new List<WeakReference>();
        bool disposed;
        internal static Func<bool> SystemPreferenceOverride=null;
        internal static readonly DependencyProperty GenerationProperty=DependencyProperty.RegisterAttached("Generation",typeof(int),typeof(StudioMotion),new PropertyMetadata(0));
        static readonly DependencyProperty OpacityManagedProperty=DependencyProperty.RegisterAttached("OpacityManaged",typeof(bool),typeof(StudioMotion),new PropertyMetadata(false));
        static readonly DependencyProperty PendingCollapseProperty=DependencyProperty.RegisterAttached("PendingCollapse",typeof(bool),typeof(StudioMotion),new PropertyMetadata(false));
        internal bool Allowed {get{return !disposed&&preference()!=false&&(SystemPreferenceOverride==null?SystemParameters.ClientAreaAnimation:SystemPreferenceOverride());}}
        internal StudioMotion(Func<bool> preference){this.preference=preference;}
        int Track(FrameworkElement element)
        {
            int version=(int)element.GetValue(GenerationProperty)+1;element.SetValue(GenerationProperty,version);
            if(!tracked.Exists(w=>w.Target==element))tracked.Add(new WeakReference(element));
            if(tracked.Count>512)tracked.RemoveAll(w=>!w.IsAlive);
            return version;
        }
        static Duration Duration(int milliseconds){return new Duration(TimeSpan.FromMilliseconds(milliseconds));}
        internal void Enter(FrameworkElement element,bool move=true)
        {
            element.SetValue(OpacityManagedProperty,true);
            Track(element);element.BeginAnimation(UIElement.OpacityProperty,null);element.Opacity=1;
            var translate=element.RenderTransform as TranslateTransform;
            if(translate==null){translate=new TranslateTransform();element.RenderTransform=translate;}
            translate.BeginAnimation(TranslateTransform.YProperty,null);translate.Y=0;
            if(!Allowed||!element.IsVisible)return;
            element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(.72,1,Duration(160)){FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
            if(move)translate.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(4,0,Duration(160)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
        }
        internal void Show(FrameworkElement element,bool visible)
        {
            element.SetValue(OpacityManagedProperty,true);
            element.SetValue(PendingCollapseProperty,!visible);
            int version=Track(element);double from=element.Opacity;element.BeginAnimation(UIElement.OpacityProperty,null);element.Opacity=1;
            element.IsHitTestVisible=visible;
            if(visible){element.Visibility=Visibility.Visible;Enter(element);return;}
            if(!Allowed||!element.IsVisible){element.Visibility=Visibility.Collapsed;return;}
            var fade=new DoubleAnimation(from,0,Duration(140)){FillBehavior=FillBehavior.Stop};
            fade.Completed+=(s,e)=>{if((int)element.GetValue(GenerationProperty)==version){element.Visibility=Visibility.Collapsed;element.BeginAnimation(UIElement.OpacityProperty,null);element.Opacity=1;}};
            element.BeginAnimation(UIElement.OpacityProperty,fade,HandoffBehavior.SnapshotAndReplace);
        }
        internal void Column(ColumnDefinition column,double width)
        {
            if(!columns.Exists(w=>w.Target==column))columns.Add(new WeakReference(column));
            double from=column.ActualWidth;column.BeginAnimation(ColumnDefinition.WidthProperty,null);column.Width=new GridLength(width);
            if(!Allowed||Math.Abs(from-width)<.5)return;
            column.BeginAnimation(ColumnDefinition.WidthProperty,new StudioColumnAnimation{From=from,To=width,Duration=Duration(180),FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
        }
        internal void Feedback(Control control)
        {
            var transform=new TranslateTransform();control.RenderTransform=transform;Track(control);
            Action change=()=>{
                double from=transform.Y;transform.BeginAnimation(TranslateTransform.YProperty,null);
                double target=control.IsEnabled&&(control.IsMouseOver||control.IsKeyboardFocusWithin)?-1:0;
                transform.Y=target;
                if(Allowed&&control.IsVisible&&control.IsEnabled)transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(from,target,Duration(120)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);
                else transform.Y=0;
            };
            control.MouseEnter+=(s,e)=>change();control.MouseLeave+=(s,e)=>change();control.IsKeyboardFocusWithinChanged+=(s,e)=>change();control.IsEnabledChanged+=(s,e)=>change();
            control.Unloaded+=(s,e)=>{transform.BeginAnimation(TranslateTransform.YProperty,null);transform.Y=0;};
        }
        internal void Fold(FrameworkElement body,bool expanded)
        {
            body.SetValue(PendingCollapseProperty,!expanded);
            int version=Track(body);double from=body.ActualHeight;
            body.BeginAnimation(FrameworkElement.HeightProperty,null);body.Height=Double.NaN;body.IsHitTestVisible=expanded;
            if(!Allowed||!body.IsVisible&& !expanded){body.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;return;}
            body.Visibility=Visibility.Visible;
            double width=body.ActualWidth>0?body.ActualWidth:600;
            body.Measure(new Size(width,Double.PositiveInfinity));double target=expanded?body.DesiredSize.Height:0;
            if(Double.IsNaN(target)||Double.IsInfinity(target))target=0;
            var animation=new DoubleAnimation(from,target,Duration(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop};
            animation.Completed+=(s,e)=>{if((int)body.GetValue(GenerationProperty)==version){body.BeginAnimation(FrameworkElement.HeightProperty,null);body.Height=Double.NaN;body.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;}};
            body.ClipToBounds=true;body.BeginAnimation(FrameworkElement.HeightProperty,animation,HandoffBehavior.SnapshotAndReplace);
        }
        internal void StopAll()
        {
            foreach(var weak in columns){var column=weak.Target as ColumnDefinition;if(column!=null)column.BeginAnimation(ColumnDefinition.WidthProperty,null);}
            foreach(var weak in tracked){var element=weak.Target as FrameworkElement;if(element==null)continue;
                element.SetValue(GenerationProperty,(int)element.GetValue(GenerationProperty)+1);if((bool)element.GetValue(OpacityManagedProperty)){element.BeginAnimation(UIElement.OpacityProperty,null);element.Opacity=1;}
                element.BeginAnimation(FrameworkElement.HeightProperty,null);
                var transform=element.RenderTransform as TranslateTransform;if(transform!=null){transform.BeginAnimation(TranslateTransform.YProperty,null);transform.Y=0;}
                if((bool)element.GetValue(PendingCollapseProperty)){element.Visibility=Visibility.Collapsed;element.SetValue(PendingCollapseProperty,false);}
            }
        }
        public void Dispose(){StopAll();disposed=true;tracked.Clear();columns.Clear();}
    }
    internal sealed class StudioColumnAnimation : AnimationTimeline
    {
        internal double From,To;
        public override Type TargetPropertyType {get{return typeof(GridLength);}}
        protected override Freezable CreateInstanceCore(){return new StudioColumnAnimation{From=From,To=To};}
        public override object GetCurrentValue(object origin,object destination,AnimationClock clock)
        {double p=clock.CurrentProgress??1;double eased=1-Math.Pow(1-p,3);return new GridLength(Math.Max(0,From+(To-From)*eased));}
    }
}

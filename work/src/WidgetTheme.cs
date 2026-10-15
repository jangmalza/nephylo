using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        private static ResourceDictionary MakeDialogResources()
        {
            return WidgetPalette.ParseResources(@"
            <ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <Style TargetType='ComboBoxItem'>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='Background' Value='Transparent'/>
                    <Setter Property='Padding' Value='10,6'/>
                    <Setter Property='MinHeight' Value='34'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                    <Setter Property='VerticalContentAlignment' Value='Center'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='ComboBoxItem'>
                                <Border x:Name='Surface' Background='{TemplateBinding Background}'
                                        Padding='{TemplateBinding Padding}' CornerRadius='4'>
                                    <Grid>
                                        <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='20'/></Grid.ColumnDefinitions>
                                        <ContentPresenter RecognizesAccessKey='True'
                                                          HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                                          VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                                        <Path x:Name='SelectedMark' Grid.Column='1' Visibility='Collapsed'
                                              Data='M 1 4 L 4 7 L 10 1' Stroke='{TemplateBinding Foreground}' StrokeThickness='1.5'
                                              StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'
                                              HorizontalAlignment='Right' VerticalAlignment='Center' IsHitTestVisible='False'/>
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsSelected' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#E9EDFF'/>
                                        <Setter TargetName='SelectedMark' Property='Visibility' Value='Visible'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsHighlighted' Value='True'>
                                        <Setter TargetName='Surface' Property='Background' Value='#EEF1FC'/>
                                        <Setter Property='Foreground' Value='#2448F5'/>
                                    </Trigger>
                                    <MultiTrigger>
                                        <MultiTrigger.Conditions>
                                            <Condition Property='IsSelected' Value='True'/>
                                            <Condition Property='IsHighlighted' Value='True'/>
                                        </MultiTrigger.Conditions>
                                        <Setter TargetName='Surface' Property='Background' Value='#DCE3FF'/>
                                    </MultiTrigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='Surface' Property='Opacity' Value='0.45'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType='ComboBox'>
                    <Setter Property='Background' Value='#F2F3EF'/>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='BorderBrush' Value='#C8CECF'/>
                    <Setter Property='BorderThickness' Value='1'/>
                    <Setter Property='Padding' Value='12,8,34,8'/>
                    <Setter Property='MinHeight' Value='38'/>
                    <Setter Property='FontSize' Value='12'/>
                    <Setter Property='MaxDropDownHeight' Value='280'/>
                    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                    <Setter Property='VerticalContentAlignment' Value='Center'/>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='ComboBox'>
                                <Grid>
                                    <Border x:Name='Surface' Background='{TemplateBinding Background}'
                                            BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'
                                            CornerRadius='6'/>
                                    <ToggleButton Focusable='False' ClickMode='Press' Background='Transparent'
                                                  IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
                                        <ToggleButton.Template>
                                            <ControlTemplate TargetType='ToggleButton'>
                                                <Border Background='Transparent'>
                                                    <Path x:Name='Chevron' Data='M 0 0 L 4 4 L 8 0' Stroke='#647070' StrokeThickness='1.5'
                                                          StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'
                                                          HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/>
                                                </Border>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property='IsChecked' Value='True'>
                                                        <Setter TargetName='Chevron' Property='Data' Value='M 0 4 L 4 0 L 8 4'/>
                                                    </Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </ToggleButton.Template>
                                    </ToggleButton>
                                    <ContentPresenter Content='{TemplateBinding SelectionBoxItem}'
                                                      ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                                                      ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'
                                                      Margin='{TemplateBinding Padding}' IsHitTestVisible='False'
                                                      HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
                                                      VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                                    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom'
                                           AllowsTransparency='True' Focusable='False' PopupAnimation='None'>
                                        <Border Background='#FBFBF8' BorderBrush='#D6D9D7' BorderThickness='1' CornerRadius='6'
                                                MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'
                                                Margin='0,6,0,0' Padding='5'>
                                            <ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto'
                                                          HorizontalScrollBarVisibility='Disabled' SnapsToDevicePixels='True'>
                                                <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
                                            </ScrollViewer>
                                        </Border>
                                    </Popup>
                                </Grid>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsMouseOver' Value='True'>
                                        <Setter TargetName='Surface' Property='BorderBrush' Value='#647070'/>
                                    </Trigger>
                                    <Trigger Property='IsKeyboardFocusWithin' Value='True'>
                                        <Setter TargetName='Surface' Property='BorderBrush' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsDropDownOpen' Value='True'>
                                        <Setter TargetName='Surface' Property='BorderBrush' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter Property='Opacity' Value='0.45'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType='CheckBox'>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='Margin' Value='0,8'/>
                    <Setter Property='FontSize' Value='13'/>
                    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='CheckBox'>
                                <Border x:Name='FocusBorder' BorderBrush='Transparent' BorderThickness='1' CornerRadius='3' Padding='3'>
                                    <Grid>
                                        <Grid.ColumnDefinitions><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                                        <Border x:Name='Box' Width='16' Height='16' VerticalAlignment='Center'
                                                Background='#F2F3EF' BorderBrush='#7E8983' BorderThickness='1' CornerRadius='3'>
                                            <Grid>
                                                <Path x:Name='Check' Visibility='Collapsed' Data='M 2 6 L 5 9 L 11 3'
                                                      Stroke='#FFFFFF' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
                                                <Border x:Name='Mixed' Visibility='Collapsed' Height='2' Margin='3,0' Background='#FFFFFF'/>
                                            </Grid>
                                        </Border>
                                        <ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsChecked' Value='True'>
                                        <Setter TargetName='Box' Property='Background' Value='#2448F5'/>
                                        <Setter TargetName='Box' Property='BorderBrush' Value='#2448F5'/>
                                        <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
                                    </Trigger>
                                    <Trigger Property='IsChecked' Value='{x:Null}'>
                                        <Setter TargetName='Box' Property='Background' Value='#2448F5'/>
                                        <Setter TargetName='Mixed' Property='Visibility' Value='Visible'/>
                                    </Trigger>
                                    <Trigger Property='IsMouseOver' Value='True'>
                                        <Setter TargetName='Box' Property='BorderBrush' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsKeyboardFocused' Value='True'>
                                        <Setter TargetName='FocusBorder' Property='BorderBrush' Value='#2448F5'/>
                                    </Trigger>
                                    <Trigger Property='IsEnabled' Value='False'>
                                        <Setter TargetName='FocusBorder' Property='Opacity' Value='0.45'/>
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType='Expander'>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='Background' Value='#FBFBF8'/>
                    <Setter Property='BorderBrush' Value='#D6D9D7'/>
                    <Setter Property='FontSize' Value='12'/>
                    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='Expander'>
                                <Border x:Name='Section' Background='{TemplateBinding Background}'
                                        BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='6'>
                                  <StackPanel>
                                    <ToggleButton x:Name='HeaderSite' Foreground='{TemplateBinding Foreground}'
                                                  IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'
                                                  Content='{TemplateBinding Header}' ContentTemplate='{TemplateBinding HeaderTemplate}'
                                                  ContentTemplateSelector='{TemplateBinding HeaderTemplateSelector}'
                                                  FontSize='{TemplateBinding FontSize}' FontWeight='SemiBold'
                                                  HorizontalContentAlignment='Stretch' MinHeight='40' FocusVisualStyle='{x:Null}'>
                                        <ToggleButton.Template>
                                            <ControlTemplate TargetType='ToggleButton'>
                                                <Border x:Name='Head' Background='Transparent' BorderBrush='Transparent'
                                                        BorderThickness='1' Padding='11,9' CornerRadius='5'>
                                                    <Grid>
                                                        <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='24'/></Grid.ColumnDefinitions>
                                                        <ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='True'>
                                                            <ContentPresenter.Resources>
                                                                <Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style>
                                                            </ContentPresenter.Resources>
                                                        </ContentPresenter>
                                                        <Path x:Name='Arrow' Grid.Column='1' Data='M 0 0 L 4 4 L 8 0'
                                                              Stroke='{TemplateBinding Foreground}' StrokeThickness='1.5'
                                                              StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'
                                                              HorizontalAlignment='Right' VerticalAlignment='Center' IsHitTestVisible='False'/>
                                                    </Grid>
                                                </Border>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property='IsChecked' Value='True'>
                                                        <Setter TargetName='Arrow' Property='Data' Value='M 0 4 L 4 0 L 8 4'/>
                                                        <Setter TargetName='Head' Property='Background' Value='#F2F3EF'/>
                                                        <Setter TargetName='Head' Property='CornerRadius' Value='5,5,0,0'/>
                                                    </Trigger>
                                                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Head' Property='Background' Value='#F2F3EF'/></Trigger>
                                                    <Trigger Property='IsKeyboardFocused' Value='True'>
                                                        <Setter TargetName='Head' Property='Background' Value='#E9EDFF'/>
                                                        <Setter TargetName='Head' Property='BorderBrush' Value='#2448F5'/>
                                                    </Trigger>
                                                    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </ToggleButton.Template>
                                    </ToggleButton>
                                    <Border x:Name='BodySurface' Visibility='Collapsed' BorderBrush='{TemplateBinding BorderBrush}'
                                            BorderThickness='0,1,0,0' Padding='12'>
                                        <ContentPresenter x:Name='ContentSite' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
                                    </Border>
                                  </StackPanel>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property='IsExpanded' Value='True'><Setter TargetName='BodySurface' Property='Visibility' Value='Visible'/></Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style TargetType='ToolTip'>
                    <Setter Property='Background' Value='#FBFBF8'/>
                    <Setter Property='Foreground' Value='#252827'/>
                    <Setter Property='BorderBrush' Value='#D6D9D7'/>
                    <Setter Property='BorderThickness' Value='1'/>
                    <Setter Property='FontFamily' Value='Segoe UI, Malgun Gothic'/>
                    <Setter Property='FontSize' Value='12'/>
                    <Setter Property='Padding' Value='11,8'/>
                    <Setter Property='MaxWidth' Value='380'/>
                    <Setter Property='Template'>
                        <Setter.Value>
                            <ControlTemplate TargetType='ToolTip'>
                                <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                                        BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}' CornerRadius='3'>
                                    <ContentPresenter>
                                        <ContentPresenter.Resources>
                                            <Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style>
                                        </ContentPresenter.Resources>
                                    </ContentPresenter>
                                </Border>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ResourceDictionary>");
        }

        private static void ApplyDialogTheme(Window dialog)
        {
            // Keep the native title bar and its standard keyboard/window controls.
            Action update = () => {
                try {
                    int enabled = WidgetPalette.IsDark ? 1 : 0;
                    IntPtr handle = new WindowInteropHelper(dialog).Handle;
                    if (handle == IntPtr.Zero) return;
                    if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                        DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                } catch (DllNotFoundException) { }
                  catch (EntryPointNotFoundException) { }
            };
            EventHandler changed = (sender,args) => update();
            dialog.SourceInitialized += changed;
            WidgetPalette.Changed += changed;
            dialog.Closed += (sender,args) => WidgetPalette.Changed -= changed;
        }
    }
}

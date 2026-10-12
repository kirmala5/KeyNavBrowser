using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KeyNavBrowser.Services;

/// <summary>
/// Элемент управления WPF <see cref="WebView2"/> создаёт <see cref="CoreWebView2Controller"/>
/// внутри себя, но не публикует это свойство, поэтому событие
/// <see cref="CoreWebView2Controller.AcceleratorKeyPressed"/> недоступно напрямую.
/// Контроллер хранится в приватном поле <c>m_webview2Base</c>, а сам
/// <c>CoreWebView2Controller</c> — публичное свойство базового класса
/// <c>WebView2Base</c>. Получаем его рефлексией.
///
/// Если в будущей версии SDK свойство станет публичным или доступ к контроллеру
/// изменится, метод вернёт null, и обработка клавиш продолжит работать через
/// внедрённый в страницу скрипт.
/// </summary>
internal static class WebView2ControllerAccessor
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static CoreWebView2Controller? TryGetController(WebView2 view)
    {
        try
        {
            var baseField = typeof(WebView2).GetField("m_webview2Base", Flags);
            if (baseField?.GetValue(view) is not { } baseInstance)
                return null;

            var controllerProperty = baseInstance.GetType()
                .GetProperty("CoreWebView2Controller", Flags);

            return controllerProperty?.GetValue(baseInstance) as CoreWebView2Controller;
        }
        catch
        {
            return null;
        }
    }
}
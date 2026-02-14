using System.Collections;
using TMPro;
using UnityEngine;

namespace FanslationStudio.Plugins.TextResizer;

// This component will monitor the text changes
public class TextChangedBehaviour : MonoBehaviour
{
    private string _lastText;
    private TextMeshProUGUI _textComponent;
    private TextResizerContract _contract;
    private Coroutine _monitoringCoroutine;

    public void SetOptions(TextResizerContract contract)
    {
        _contract = contract;
    }

    private void Awake()
    {
        _textComponent = GetComponent<TextMeshProUGUI>();
        if (_textComponent != null)
        {
            _lastText = _textComponent.text;
            StartMonitoring(_textComponent);
        }
    }

    public void StartMonitoring(TextMeshProUGUI textComponent)
    {
        if (_monitoringCoroutine != null)
        {
            // If monitoring is already started, do not start it again
            return;
        }

        _textComponent = textComponent;
        _lastText = textComponent.text;

        _monitoringCoroutine = StartCoroutine(MonitorTextChanges());
    }

    private IEnumerator MonitorTextChanges()
    {
        while (_textComponent != null)
        {
            if (_textComponent.text != _lastText)
            {
                _lastText = _textComponent.text;

                if (_contract.AllowLeftTrimText)
                    _textComponent.text = _textComponent.text.TrimStart(); // Trim leading spaces
            }

            yield return null; // Check every frame
        }
    }
}

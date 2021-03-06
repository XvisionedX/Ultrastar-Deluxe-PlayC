using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UniRx;
using System;
using System.Linq;

public class AmplificationSlider : TextItemSlider<int>
{
    private IDisposable disposable;

    protected override void Awake()
    {
        base.Awake();
        Items = new List<int> { 0, 3, 6, 9, 12, 15, 18 };
    }

    public void SetMicProfile(MicProfile micProfile)
    {
        if (disposable != null)
        {
            disposable.Dispose();
        }

        Selection.Value = Items.Where(it => it == micProfile.Amplification).FirstOrDefault().OrIfNull(0);
        disposable = Selection.Subscribe(newValue => micProfile.Amplification = newValue);
    }

    protected override string GetDisplayString(int value)
    {
        return $"{value} dB";
    }

}

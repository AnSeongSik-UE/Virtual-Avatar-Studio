using UnityEngine;

public class EMAFilter
{
    private Vector3 _lastV3;
    private float[] _lastArray;
    private float _alpha;
    private bool _initialized;

    public EMAFilter(float alpha)
    {
        _alpha = alpha;
        _initialized = false;
    }

    public Vector3 Filter(Vector3 current)
    {
        if (!_initialized)
        {
            _lastV3 = current;
            _initialized = true;
            return current;
        }
        _lastV3 = Vector3.Lerp(_lastV3, current, _alpha);
        return _lastV3;
    }

    public float[] Filter(float[] current)
    {
        if (current == null) return null;
        if (!_initialized || _lastArray == null || _lastArray.Length != current.Length)
        {
            _lastArray = (float[])current.Clone();
            _initialized = true;
            return _lastArray;
        }

        for (int i = 0; i < current.Length; i++)
        {
            _lastArray[i] = Mathf.Lerp(_lastArray[i], current[i], _alpha);
        }
        return _lastArray;
    }

    public void Reset()
    {
        _initialized = false;
    }
}

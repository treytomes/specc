namespace IronLlm.Learning;

// Two-layer feed-forward network for one node kind.
// Input:  [nodeEmb(1024) ∥ neighborMean(1024)] → 2048 floats
// Hidden: Linear(2048 → 256) + ReLU
// Output: Linear(256 → 1024)
public class NodeMlp
{
    public float[][] W1 { get; init; }  // [256][2048]
    public float[]   B1 { get; init; }  // [256]
    public float[][] W2 { get; init; }  // [1024][256]
    public float[]   B2 { get; init; }  // [1024]

    public const int InputDim  = 2048;
    public const int HiddenDim = 256;
    public const int OutputDim = 1024;

    public NodeMlp(float[][] w1, float[] b1, float[][] w2, float[] b2)
    {
        W1 = w1; B1 = b1; W2 = w2; B2 = b2;
    }

    public float[] Forward(float[] nodeEmb, float[] neighborMean)
    {
        // Concatenate inputs
        var input = new float[InputDim];
        Array.Copy(nodeEmb,     0, input, 0,    nodeEmb.Length);
        Array.Copy(neighborMean, 0, input, 1024, neighborMean.Length);

        // Hidden layer: h = ReLU(W1 * input + B1)
        var hidden = new float[HiddenDim];
        for (var i = 0; i < HiddenDim; i++)
        {
            var sum = B1[i];
            var row = W1[i];
            for (var j = 0; j < InputDim; j++)
                sum += row[j] * input[j];
            hidden[i] = sum > 0f ? sum : 0f; // ReLU
        }

        // Output layer: out = W2 * hidden + B2
        var output = new float[OutputDim];
        for (var i = 0; i < OutputDim; i++)
        {
            var sum = B2[i];
            var row = W2[i];
            for (var j = 0; j < HiddenDim; j++)
                sum += row[j] * hidden[j];
            output[i] = sum;
        }

        return output;
    }

    // Xavier uniform initialisation: ±sqrt(6 / (fan_in + fan_out))
    public static NodeMlp CreateRandom(Random rng)
    {
        var w1 = XavierMatrix(rng, HiddenDim, InputDim);
        var b1 = new float[HiddenDim];
        var w2 = XavierMatrix(rng, OutputDim, HiddenDim);
        var b2 = new float[OutputDim];
        return new NodeMlp(w1, b1, w2, b2);
    }

    private static float[][] XavierMatrix(Random rng, int rows, int cols)
    {
        var limit = MathF.Sqrt(6f / (cols + rows));
        var matrix = new float[rows][];
        for (var i = 0; i < rows; i++)
        {
            matrix[i] = new float[cols];
            for (var j = 0; j < cols; j++)
                matrix[i][j] = (float)(rng.NextDouble() * 2 - 1) * limit;
        }
        return matrix;
    }
}

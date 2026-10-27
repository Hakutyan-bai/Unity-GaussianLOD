// Aholo data layout and 3DGS SH convention; Unity covariance projection implementation.
struct GaussianRecord
{
    float4 positionOpacity;
    float4 covarianceA;
    float4 covarianceB;
    float4 color;
    float4 sh[12];
};

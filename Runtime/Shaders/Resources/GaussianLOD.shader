Shader "Gaussian Splat LOD/Gaussian"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "Gaussian"
            ZWrite Off ZTest LEqual Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "GaussianLODCommon.hlsl"
            StructuredBuffer<GaussianRecord> _Splats;
            StructuredBuffer<uint2> _Visible, _Order;
            StructuredBuffer<float> _Fades;
            float4x4 _Model, _View, _Projection;
            float4 _Viewport, _LocalCamera;
            float _RenderScale, _OpacityMultiplier;
            int _SHDegree, _DebugLOD, _Orthographic, _UseCustomProjection;
            float ScalarSH(GaussianRecord g,int i) { return g.sh[i/4][i%4]; }
            float3 SH(GaussianRecord g,int c) { int at=c*3;return float3(ScalarSH(g,at),ScalarSH(g,at+1),ScalarSH(g,at+2)); }
            float3 Shade(GaussianRecord g,float3 d)
            {
                float x=d.x,y=d.y,z=d.z,xx=x*x,yy=y*y,zz=z*z;
                float3 c=g.color.rgb;
                if(_SHDegree>=1) c+=0.4886025119*(-y*SH(g,0)+z*SH(g,1)-x*SH(g,2));
                if(_SHDegree>=2) c+=1.092548431*x*y*SH(g,3)-1.092548431*y*z*SH(g,4)+0.3153915653*(2*zz-xx-yy)*SH(g,5)-1.092548431*x*z*SH(g,6)+0.5462742153*(xx-yy)*SH(g,7);
                if(_SHDegree>=3) c+=-0.5900435899*y*(3*xx-yy)*SH(g,8)+2.890611443*x*y*z*SH(g,9)-0.4570457995*y*(4*zz-xx-yy)*SH(g,10)+0.3731763326*z*(2*zz-3*xx-3*yy)*SH(g,11)-0.4570457995*x*(4*zz-xx-yy)*SH(g,12)+1.445305721*z*(xx-yy)*SH(g,13)-0.5900435899*x*(xx-3*yy)*SH(g,14);
                return max(c,0);
            }
            float3 LevelColor(uint l)
            {
                if(l==0)return float3(1,.2,.05);if(l==1)return float3(1,.85,.1);if(l==2)return float3(.1,1,.3);if(l==3)return float3(0,.7,1);return float3(.3,.2,1);
            }
            struct Varyings { float4 pos:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR0;nointerpolation float fade:TEXCOORD1; };
            Varyings vert(uint vertex:SV_VertexID,uint instance:SV_InstanceID)
            {
                Varyings o=(Varyings)0;
                uint2 ref=_Visible[_Order[instance].y];
                GaussianRecord g=_Splats[ref.x];
                // Match Unity's actual render target (Game View, Scene View, URP intermediate,
                // or explicit RT). camera.targetTexture alone cannot determine its Y flip.
                float4x4 projection=_UseCustomProjection!=0?_Projection:UNITY_MATRIX_P;
                float4x4 mv=mul(_View,_Model);
                float3 view=mul(mv,float4(g.positionOpacity.xyz,1)).xyz;
                if(view.z>=-0.001) {o.pos=float4(2,2,2,1);return o;}
                float3 a=g.covarianceA.xyz,b=g.covarianceB.xyz;
                float3x3 cov=float3x3(a.x,a.y,a.z,a.y,b.x,b.y,a.z,b.y,b.z);
                float3x3 r=(float3x3)mv;cov=mul(r,mul(cov,transpose(r)))*(_RenderScale*_RenderScale);
                float fx=projection._m00*_Viewport.x*.5,fy=projection._m11*_Viewport.y*.5;
                float3 jx,jy;
                if(_Orthographic!=0) {jx=float3(fx,0,0);jy=float3(0,fy,0);}
                else {
                    float z=view.z;
                    float vx=clamp(view.x/z,-1.3/max(abs(projection._m00),.001),1.3/max(abs(projection._m00),.001))*z;
                    float vy=clamp(view.y/z,-1.3/max(abs(projection._m11),.001),1.3/max(abs(projection._m11),.001))*z;
                    jx=float3(fx/z,0,-fx*vx/(z*z));jy=float3(0,fy/z,-fy*vy/(z*z));
                }
                float aa=dot(jx,mul(cov,jx))+.3,bb=dot(jx,mul(cov,jy)),cc=dot(jy,mul(cov,jy))+.3;
                float mid=(aa+cc)*.5,delta=sqrt(max(0,(aa-cc)*(aa-cc)*.25+bb*bb));
                float l1=max(.01,mid+delta),l2=max(.01,mid-delta);
                float2 axis=abs(bb)>1e-6?normalize(float2(bb,l1-aa)):(aa>=cc?float2(1,0):float2(0,1));
                float2 corner;
                if(vertex==0||vertex==3)corner=float2(-1,-1);else if(vertex==1)corner=float2(1,-1);else if(vertex==2||vertex==4)corner=float2(1,1);else corner=float2(-1,1);
                float2 uv=corner*3.7;
                float2 displacement=uv.x*sqrt(l1)*axis+uv.y*sqrt(l2)*float2(-axis.y,axis.x);
                float4 clipPos=mul(projection,float4(view,1));clipPos.xy+=displacement*2/_Viewport.xy*clipPos.w;
                o.pos=clipPos;o.uv=uv;
                float3 d=g.positionOpacity.xyz-_LocalCamera.xyz;d*=rsqrt(max(dot(d,d),1e-20));
                float3 rgb=_DebugLOD!=0?LevelColor(ref.y&255):Shade(g,d);
                o.fade=_Fades[ref.y>>8];
                #ifndef UNITY_COLORSPACE_GAMMA
                    rgb=GammaToLinearSpace(rgb);
                #endif
                o.color=float4(rgb,saturate(g.positionOpacity.w*_OpacityMultiplier));return o;
            }
            half4 frag(Varyings i):SV_Target
            {
                float p=dot(i.uv,i.uv);clip(13.69-p);
                float alpha=min(.99,i.color.a*exp(-.5*p));
                // Interpolate optical depth, preserving opacity for identical coarse/fine layers.
                // A linear alpha fade darkens overlaps and makes a model breathe at every switch.
                alpha=1-pow(max(1-alpha,1e-6),i.fade);clip(alpha-1.0/255.0);
                return half4(i.color.rgb*alpha,alpha);
            }
            ENDHLSL
        }
    }
}

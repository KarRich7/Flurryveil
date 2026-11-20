using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing.Imaging;

namespace QuietSnow;

/// <summary>Full-resolution GPU particles, with cached original window decorations.</summary>
internal sealed unsafe class GpuSnowRenderer : IDisposable
{
    nint device,context,swap,composition,target,visual,back,renderTarget,vertexShader,pixelShader,inputLayout,instances,constants,blendState,rasterState;
    nint context1, dirtyTexture, dirtyView;
    nint weatherTexture, weatherView;
    nint opacityEffect;
    int previousOpacity = 100;
    internal float CompositionOpacity => previousOpacity / 100f;
    readonly DirtyTiles dirty;
    GpuParticle[] previousParticles = [];
    int previousCount, previousFlakes, previousBackground, previousDust;
    bool forceFullPresent = true;
    bool partialPresentation;
    internal long PresentedPixels { get; private set; }
    internal bool UsesPartialPresentation => partialPresentation;
    GpuRasterLayer? background,foreground;
    GpuParticle[] particles = new GpuParticle[1024];
    readonly List<Rectangle> exclusions=[];
    readonly Rectangle[] previousExclusions = new Rectangle[8];
    int previousExclusionCount = -1;
    readonly Size size;
    public int LastParticleCount {get;private set;}
    public long ParticleUploadBytes {get;private set;}
    public long RasterUploadBytes => (background?.UploadBytes??0)+(foreground?.UploadBytes??0);
    public long RasterSurfaceBytes => (background?.SurfaceBytes ?? 0) + (foreground?.SurfaceBytes ?? 0);
    internal int RasterPaintCount => (background?.PaintCount ?? 0) + (foreground?.PaintCount ?? 0);
    public Size RenderSize=>size;
    internal uint AdapterVendorId { get; }
    internal bool UsesDisplayAdapter { get; }
    public static nint Method(nint obj,int slot)=>(*(nint**)obj)[slot];
    static void Check(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
    public static void Release(ref nint obj){if(obj!=0){((delegate* unmanaged[Stdcall]<nint,uint>)Method(obj,2))(obj);obj=0;}}
    static nint Query(nint obj,Guid iid){nint result=0;Check(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Method(obj,0))(obj,&iid,&result));return result;}
    public GpuSnowRenderer(nint window,Size size)
    {
        this.size=size;dirty=new DirtyTiles(size);nint dxgi=0,adapter=0,factory=0,displayAdapter=0;
        try
        {
            if (Environment.GetEnvironmentVariable("QUIETSNOW_DEFAULT_ADAPTER") != "1")
                displayAdapter = FindDisplayAdapter(window);
            if (displayAdapter != 0)
            {
                int result = D3D11CreateDevice(displayAdapter, 0, 0, 0x20, 0, 0, 7, out device, out _, out context);
                UsesDisplayAdapter = result >= 0;
                if (!UsesDisplayAdapter) { Release(ref context); Release(ref device); }
            }
            if (device == 0)
                Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            dxgi=Query(device,new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
            Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Method(dxgi,7))(dxgi,&adapter));
            AdapterDescription adapterDescription = default;
            if (((delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Method(adapter, 8))(adapter, &adapterDescription) >= 0)
                AdapterVendorId = adapterDescription.Vendor;
            Guid factoryId=new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
            Check(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Method(adapter,6))(adapter,&factoryId,&factory));
            var desc=new SwapDesc{Width=(uint)size.Width,Height=(uint)size.Height,Format=87,SampleCount=1,Usage=0x20,Count=2,SwapEffect=3,AlphaMode=1};
            fixed(nint* output=&swap)Check(((delegate* unmanaged[Stdcall]<nint,nint,SwapDesc*,nint,nint*,int>)Method(factory,24))(factory,device,&desc,0,output));
            Guid compositionId=new("c37ea93a-e7aa-450d-b16f-9746cb0407f3");Check(DCompositionCreateDevice(dxgi,in compositionId,out composition));
            fixed(nint* output=&target)Check(((delegate* unmanaged[Stdcall]<nint,nint,int,nint*,int>)Method(composition,6))(composition,window,1,output));
            fixed(nint* output=&visual)Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Method(composition,7))(composition,output));
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Method(visual,15))(visual,swap));
            Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Method(target,3))(target,visual));
            Check(((delegate* unmanaged[Stdcall]<nint,int>)Method(composition,3))(composition));
            Guid textureId=new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            fixed(nint* output=&back)Check(((delegate* unmanaged[Stdcall]<nint,uint,Guid*,nint*,int>)Method(swap,9))(swap,0,&textureId,output));
            fixed(nint* output=&renderTarget)Check(((delegate* unmanaged[Stdcall]<nint,nint,nint,nint*,int>)Method(device,9))(device,back,0,output));
            CreatePipeline();
            if (Environment.GetEnvironmentVariable("QUIETSNOW_FULL_PRESENT") != "1")
            {
                try
                {
                    context1 = Query(context, new Guid("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1"));
                    var maskDesc = new TextureDesc { Width = (uint)dirty.Columns, Height = (uint)dirty.Rows,
                        Mips = 1, Array = 1, Format = 62, SampleCount = 1, Bind = 8 };
                    fixed (nint* output = &dirtyTexture)
                        Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Method(device, 5))(device, &maskDesc, 0, output));
                    fixed (nint* output = &dirtyView)
                        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(device, 7))(device, dirtyTexture, 0, output));
                    partialPresentation = true;
                }
                catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
                { Release(ref dirtyView); Release(ref dirtyTexture); Release(ref context1); }
            }
            BindPipeline();
            background=new GpuRasterLayer(size);foreground=new GpuRasterLayer(size);
        }
        catch{Dispose();throw;}
        finally{Release(ref factory);Release(ref adapter);Release(ref dxgi);Release(ref displayAdapter);}
    }
    static nint FindDisplayAdapter(nint window)
    {
        nint monitor = MonitorFromWindow(window, 2);
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (monitor == 0 || CreateDXGIFactory1(in factoryId, out nint factory) < 0) return 0;
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                nint candidate = 0;
                int result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(factory, 12))(factory, adapterIndex, &candidate);
                if (result < 0) return 0;
                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        nint output = 0;
                        result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(candidate, 7))(candidate, outputIndex, &output);
                        if (result < 0) break;
                        try
                        {
                            OutputDescription description = default;
                            result = ((delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)Method(output, 7))(output, &description);
                            if (result >= 0 && description.AttachedToDesktop != 0 && description.Monitor == monitor)
                            {
                                nint match = candidate;
                                candidate = 0;
                                return match;
                            }
                        }
                        finally { Release(ref output); }
                    }
                }
                finally { Release(ref candidate); }
            }
        }
        finally { Release(ref factory); }
    }

    void CreatePipeline()
    {
        nint vs=Compile("VS","vs_4_0"),ps=0;
        try
        {
            ps=Compile("PS","ps_4_0");nint vsCode=BlobData(vs);nuint vsLength=BlobSize(vs);
            fixed(nint* output=&vertexShader)Check(((delegate* unmanaged[Stdcall]<nint,nint,nuint,nint,nint*,int>)Method(device,12))(device,vsCode,vsLength,0,output));
            fixed(nint* output=&pixelShader)Check(((delegate* unmanaged[Stdcall]<nint,nint,nuint,nint,nint*,int>)Method(device,15))(device,BlobData(ps),BlobSize(ps),0,output));
            nint rect=Marshal.StringToHGlobalAnsi("RECT"),color=Marshal.StringToHGlobalAnsi("COLOR"),mode=Marshal.StringToHGlobalAnsi("MODE");
            try
            {
                InputElement* layout=stackalloc InputElement[3];
                layout[0]=new InputElement{Name=rect,Format=2,Class=1,Step=1};layout[1]=new InputElement{Name=color,Format=28,Offset=16,Class=1,Step=1};layout[2]=new InputElement{Name=mode,Format=6,Offset=20,Class=1,Step=1};
                fixed(nint* output=&inputLayout)Check(((delegate* unmanaged[Stdcall]<nint,InputElement*,uint,nint,nuint,nint*,int>)Method(device,11))(device,layout,3,vsCode,vsLength,output));
            }
            finally{Marshal.FreeHGlobal(rect);Marshal.FreeHGlobal(color);Marshal.FreeHGlobal(mode);}
        }
        finally{Release(ref ps);Release(ref vs);}
        instances=CreateBuffer(particles.Length*sizeof(GpuParticle),1);constants=CreateBuffer(16*9,4);
        byte* blend=stackalloc byte[264];new Span<byte>(blend,264).Clear();int* fields=(int*)(blend+8);fields[0]=1;fields[1]=2;fields[2]=6;fields[3]=1;fields[4]=2;fields[5]=6;fields[6]=1;blend[36]=15;
        fixed(nint* output=&blendState)Check(((delegate* unmanaged[Stdcall]<nint,byte*,nint*,int>)Method(device,20))(device,blend,output));
        var raster=new RasterDesc{Fill=3,Cull=1,DepthClip=1};
        fixed(nint* output=&rasterState)Check(((delegate* unmanaged[Stdcall]<nint,RasterDesc*,nint*,int>)Method(device,22))(device,&raster,output));
    }
    nint CreateBuffer(int bytes,uint bind)
    {
        var desc=new BufferDesc{Bytes=(uint)bytes,Usage=2,Bind=bind,Cpu=0x10000};nint result=0;
        Check(((delegate* unmanaged[Stdcall]<nint,BufferDesc*,nint,nint*,int>)Method(device,3))(device,&desc,0,&result));return result;
    }
    public void CreateTexture(Size size,out nint texture,out nint view)
    {
        var desc=new TextureDesc{Width=(uint)size.Width,Height=(uint)size.Height,Mips=1,Array=1,Format=87,SampleCount=1,Bind=8};nint tex=0,srv=0;
        Check(((delegate* unmanaged[Stdcall]<nint,TextureDesc*,nint,nint*,int>)Method(device,5))(device,&desc,0,&tex));
        try{Check(((delegate* unmanaged[Stdcall]<nint,nint,nint,nint*,int>)Method(device,7))(device,tex,0,&srv));}catch{Release(ref tex);throw;}
        texture=tex;view=srv;
    }
    public void UpdateTexture(nint texture,Rectangle rect,nint data,int stride)
    {
        var box=new Box{Left=(uint)rect.Left,Top=(uint)rect.Top,Right=(uint)rect.Right,Bottom=(uint)rect.Bottom,Back=1};
        ((delegate* unmanaged[Stdcall]<nint,nint,uint,Box*,nint,uint,uint,void>)Method(context,48))(context,texture,0,&box,data,(uint)stride,0);
    }
    void BindPipeline()
    {
        var vp=new Viewport{Width=size.Width,Height=size.Height,Max=1};((delegate* unmanaged[Stdcall]<nint,uint,Viewport*,void>)Method(context,44))(context,1,&vp);
        ((delegate* unmanaged[Stdcall]<nint,nint,void>)Method(context,43))(context,rasterState);
        ((delegate* unmanaged[Stdcall]<nint,nint,float*,uint,void>)Method(context,35))(context,blendState,null,uint.MaxValue);
        ((delegate* unmanaged[Stdcall]<nint,nint,void>)Method(context,17))(context,inputLayout);
        BindInstances();
        ((delegate* unmanaged[Stdcall]<nint,uint,void>)Method(context,24))(context,4);
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,uint,void>)Method(context,11))(context,vertexShader,0,0);
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,uint,void>)Method(context,9))(context,pixelShader,0,0);
        nint cb=constants;((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,void>)Method(context,7))(context,0,1,&cb);((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,void>)Method(context,16))(context,0,1,&cb);
    }

    void BindInstances()
    {
        uint stride = (uint)sizeof(GpuParticle), offset = 0;
        nint buffer = instances;
        ((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,uint*,uint*,void>)Method(context,18))(context,0,1,&buffer,&stride,&offset);
    }

    void UpdateExclusions(SnowApp app, Rectangle viewport)
    {
        app.GpuExclusions(exclusions);
        int count = Math.Min(previousExclusions.Length, exclusions.Count);
        Span<Rectangle> local = stackalloc Rectangle[count];
        for (int i = 0; i < count; i++)
        {
            var rect = exclusions[i];
            rect.Offset(-viewport.Left, -viewport.Top);
            local[i] = rect;
        }
        if (count == previousExclusionCount && local.SequenceEqual(previousExclusions.AsSpan(0, count))) return;
        local.CopyTo(previousExclusions);
        previousExclusionCount = count;
        float* uniform = stackalloc float[36];
        new Span<float>(uniform, 36).Clear();
        uniform[0] = size.Width; uniform[1] = size.Height; uniform[2] = count;
        uniform[3] = partialPresentation ? 1f / DirtyTiles.Edge : 0;
        forceFullPresent = true;
        for (int i = 0; i < count; i++)
        {
            var rect = local[i];
            int offset = 4 + i * 4;
            uniform[offset] = rect.Left; uniform[offset + 1] = rect.Top;
            uniform[offset + 2] = rect.Right; uniform[offset + 3] = rect.Bottom;
        }
        UploadBuffer(constants, uniform, 144);
    }

    void UpdateOpacity(int opacity)
    {
        if (opacity == previousOpacity) return;
        if (opacityEffect == 0)
        {
            fixed (nint* effect = &opacityEffect)
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(composition, 23))(composition, effect));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(visual, 10))(visual, opacityEffect));
        }
        // Fade the completed visual once, including overlapping particles and
        // cached decorations, without touching their pixels or forcing a frame.
        // ABI slot 3 takes an animation pointer; slot 4 is the float overload.
        Check(((delegate* unmanaged[Stdcall]<nint, float, int>)Method(opacityEffect, 4))(opacityEffect, opacity / 100f));
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(composition, 3))(composition));
        previousOpacity = opacity;
    }

    void EnsureWeatherAtlas(SnowApp app)
    {
        if (weatherView == 0)
        {
            var atlas = app.WeatherAtlas;
            CreateTexture(atlas.Size, out weatherTexture, out weatherView);
            var bits = atlas.LockBits(new Rectangle(Point.Empty, atlas.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { UpdateTexture(weatherTexture, new Rectangle(Point.Empty, atlas.Size), bits.Scan0, bits.Stride); }
            finally { atlas.UnlockBits(bits); }
        }
        nint view = weatherView;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, 8))(context, 2, 1, &view);
    }

    public int Render(SnowApp app, Rectangle viewport, bool present = true)
    {
        PresentedPixels = 0;
        ParticleUploadBytes = 0;
        SetTexture(0);
        UpdateOpacity(app.EffectOpacity);
        bool seasonal = app.Weather is WeatherMode.AutumnLeaves or WeatherMode.CherryBlossom;
        if (seasonal) EnsureWeatherAtlas(app);
        background!.Update(app, viewport, SnowPaintLayer.Background, this);
        foreground!.Update(app, viewport, SnowPaintLayer.Foreground, this);

        int required = app.GpuFlakes.Count + app.GpuDust.Count + background.VisibleBlocks.Count + foreground.VisibleBlocks.Count;
        if (required > particles.Length)
        {
            int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)required);
            nint buffer = CreateBuffer(capacity * sizeof(GpuParticle), 1);
            Release(ref instances);
            instances = buffer;
            BindInstances();
            particles = new GpuParticle[capacity];
        }

        int count=0;
        foreach(var f in app.GpuFlakes)
        {
            int margin = app.Weather == WeatherMode.Snow ? 4 : 24;
            if(f.Region!=viewport||f.Fade<=0||f.X+margin<viewport.Left||f.X-margin>viewport.Right||f.Y+margin<viewport.Top||f.Y-margin>viewport.Bottom)continue;
            uint alpha=(uint)(f.Opacity*f.Fade*(f.Settling>0?Math.Min(1,f.Settling/.2f):1)*255);
            if (seasonal)
                particles[count++] = new GpuParticle { Rect = new(f.X - viewport.Left - f.Size, f.Y - viewport.Top - f.Size, f.Size * 2, f.Size * 2),
                    Color = 0x00ffffff | alpha << 24, Mode = new(2, f.Variant, f.Angle) };
            else if (app.Weather == WeatherMode.Rain)
                particles[count++] = new GpuParticle { Rect = new(f.X - viewport.Left - 2, f.Y - viewport.Top - f.Size, 4, f.Size),
                    Color = 0x00f2cda0 | alpha << 24, Mode = new(-1, app.RainSlant, 0) };
            else particles[count++]=new GpuParticle{Rect=new(f.X-viewport.Left-f.Size/2,f.Y-viewport.Top-f.Size/2,f.Size,f.Size),Color=0x00fffdfb | alpha << 24};
        }
        int endFlakes = count;
        AppendLayer(background, ref count);
        int endBackground = count;
        foreach(var dust in app.GpuDust)
        {
            if(!viewport.Contains((int)dust.X,(int)dust.Y))continue;
            float trail=Math.Min(4,Math.Abs(dust.Vy)*.016f);
            uint alpha=(uint)(235*(dust.Settles?1:Math.Clamp(dust.Life*2.8f,0,1)));
            if (seasonal)
                particles[count++] = new GpuParticle { Rect = new(dust.X - viewport.Left - dust.Size, dust.Y - viewport.Top - dust.Size, dust.Size * 2, dust.Size * 2),
                    Color = 0x00ffffff | alpha << 24, Mode = new(2, dust.Variant, dust.Angle) };
            else particles[count++]=new GpuParticle{Rect=new(dust.X-viewport.Left,dust.Y-viewport.Top-trail/2,dust.Size,dust.Size+trail),Color=(app.Weather == WeatherMode.Rain ? 0x00f2cda0u : 0x00ffffffu) | alpha << 24};
        }
        int endDust = count;
        AppendLayer(foreground, ref count);
        LastParticleCount = endFlakes + endDust - endBackground;

        UpdateExclusions(app, viewport);
        bool backgroundChanged = background.UploadBytes != 0 || !Same(endFlakes, endBackground, previousFlakes, previousBackground);
        bool foregroundChanged = foreground.UploadBytes != 0 || !Same(endDust, count, previousDust, previousCount);
        bool particlesChanged = !Same(0, endFlakes, 0, previousFlakes) || !Same(endBackground, endDust, previousBackground, previousDust);
        if (present && !forceFullPresent && !backgroundChanged && !foregroundChanged && !particlesChanged) return 0;
        dirty.Reset();
        if (!partialPresentation || !present || forceFullPresent) dirty.Full();
        else
        {
            Mark(particles, 0, endFlakes); Mark(particles, endBackground, endDust);
            Mark(previousParticles, 0, previousFlakes); Mark(previousParticles, previousBackground, previousDust);
            if (backgroundChanged) { Mark(particles, endFlakes, endBackground); Mark(previousParticles, previousFlakes, previousBackground); }
            if (foregroundChanged) { Mark(particles, endDust, count); Mark(previousParticles, previousDust, previousCount); }
            dirty.Build();
        }
        if (present && dirty.Rectangles.Count == 0) return 0;
        if (partialPresentation)
        {
            fixed (byte* mask = dirty.Mask)
                UpdateTexture(dirtyTexture, new Rectangle(0, 0, dirty.Columns, dirty.Rows), (nint)mask, dirty.Columns);
            nint view = dirtyView;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Method(context, 8))(context, 1, 1, &view);
        }
        float* clear = stackalloc float[4] { 0, 0, 0, 0 };
        if (partialPresentation && present && !forceFullPresent)
        {
            // Present1 requires every dirty pixel to be replaced, and forbids
            // writes outside those rectangles. ClearView clears them in one
            // call; the shader's tile mask confines the following draws.
            fixed (Native.RECT* rectangles = CollectionsMarshal.AsSpan(dirty.Rectangles))
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, Native.RECT*, uint, void>)Method(context1, 132))
                    (context1, renderTarget, clear, rectangles, (uint)dirty.Rectangles.Count);
        }
        else ((delegate* unmanaged[Stdcall]<nint,nint,float*,void>)Method(context,50))(context, renderTarget, clear);
        nint rt = renderTarget;
        ((delegate* unmanaged[Stdcall]<nint,uint,nint*,nint,void>)Method(context,33))(context, 1, &rt, 0);

        if (count > 0)
        {
            int bytes = count * sizeof(GpuParticle);
            fixed (GpuParticle* data = particles) UploadBuffer(instances, data, bytes);
            ParticleUploadBytes = bytes;
            Draw(0, endFlakes, 0);
            Draw(endFlakes, endBackground, background.View);
            Draw(endBackground, endDust, 0);
            Draw(endDust, count, foreground.View);
        }

        if (!present) { forceFullPresent = true; return 0; }
        int result;
        if (partialPresentation)
        {
            fixed (Native.RECT* rectangles = CollectionsMarshal.AsSpan(dirty.Rectangles))
            {
                var parameters = new PresentParameters { Count = (uint)dirty.Rectangles.Count, Rectangles = (nint)rectangles };
                result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, PresentParameters*, int>)Method(swap, 22))(swap, 0, 0, &parameters);
            }
        }
        else result = ((delegate* unmanaged[Stdcall]<nint,uint,uint,int>)Method(swap,8))(swap, 0, 0);
        if (partialPresentation && result == unchecked((int)0x887a0001)) // DXGI_ERROR_INVALID_CALL
        {
            partialPresentation = false;
            forceFullPresent = true;
            previousExclusionCount = -1;
            return Render(app, viewport, present);
        }
        if (result == 0)
        {
            if (previousParticles.Length < count) Array.Resize(ref previousParticles, particles.Length);
            particles.AsSpan(0, count).CopyTo(previousParticles);
            previousCount = count; previousFlakes = endFlakes; previousBackground = endBackground; previousDust = endDust;
            forceFullPresent = false;
            PresentedPixels = dirty.Pixels;
        }
        else forceFullPresent = true;
        return result;

        void Mark(GpuParticle[] source, int start, int end)
        {
            for (int i = start; i < end; i++) dirty.Mark(source[i].Rect);
        }
        bool Same(int start, int end, int oldStart, int oldEnd) => end - start == oldEnd - oldStart &&
            MemoryMarshal.AsBytes(particles.AsSpan(start, end - start)).SequenceEqual(
                MemoryMarshal.AsBytes(previousParticles.AsSpan(oldStart, oldEnd - oldStart)));
    }

    void AppendLayer(GpuRasterLayer layer, ref int count)
    {
        foreach (var block in layer.VisibleBlocks)
        {
            var screen = block.Screen;
            particles[count++] = new GpuParticle
            {
                Rect = new Vector4(screen.X, screen.Y, screen.Width, screen.Height),
                Mode = new Vector3(1, block.TextureOrigin.X - screen.X, block.TextureOrigin.Y - screen.Y)
            };
        }
    }

    void SetTexture(nint view)
    {
        ((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,void>)Method(context,8))(context, 0, 1, &view);
    }

    void Draw(int start, int end, nint texture)
    {
        if (start == end) return;
        SetTexture(texture);
        ((delegate* unmanaged[Stdcall]<nint,uint,uint,uint,uint,void>)Method(context,21))(context, 6, (uint)(end - start), 0, (uint)start);
    }
    void UploadBuffer(nint buffer,void* data,int bytes)
    {
        Mapped mapped;Check(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)Method(context,14))(context,buffer,0,4,0,&mapped));
        Buffer.MemoryCopy(data,(void*)mapped.Data,bytes,bytes);((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)Method(context,15))(context,buffer,0);
    }
    public Bitmap Capture() => CaptureTexture(back);
    internal Bitmap CapturePresented()
    {
        Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); nint presented = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Method(swap, 9))(swap, 1, &textureId, &presented));
        try { return CaptureTexture(presented); }
        finally { Release(ref presented); }
    }
    Bitmap CaptureTexture(nint source)
    {
        var desc=new TextureDesc{Width=(uint)size.Width,Height=(uint)size.Height,Mips=1,Array=1,Format=87,SampleCount=1,Usage=3,Cpu=0x20000};nint staging=0;
        Check(((delegate* unmanaged[Stdcall]<nint,TextureDesc*,nint,nint*,int>)Method(device,5))(device,&desc,0,&staging));
        try
        {
            ((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)Method(context,47))(context,staging,source);Mapped mapped;
            Check(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)Method(context,14))(context,staging,0,1,0,&mapped));
            var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);var data=image.LockBits(new Rectangle(Point.Empty,size),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
            for(int y=0;y<size.Height;y++)Buffer.MemoryCopy((void*)(mapped.Data+y*(int)mapped.RowPitch),(void*)(data.Scan0+y*data.Stride),data.Stride,size.Width*4);
            image.UnlockBits(data);((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)Method(context,15))(context,staging,0);return image;
        }
        finally{Release(ref staging);}
    }
    static nint BlobData(nint blob)=>((delegate* unmanaged[Stdcall]<nint,nint>)Method(blob,3))(blob);
    static nuint BlobSize(nint blob)=>((delegate* unmanaged[Stdcall]<nint,nuint>)Method(blob,4))(blob);
    static nint Compile(string entry,string target)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(Shader);nint code=0,errors=0;
        fixed(byte* data=bytes)
        {
            int hr=D3DCompile((nint)data,(nuint)bytes.Length,"QuietSnow GPU",0,0,entry,target,0x8000,0,out code,out errors);
            if(hr<0){string message=errors!=0?Marshal.PtrToStringAnsi(BlobData(errors),(int)BlobSize(errors))!:"Shader compilation failed";Release(ref errors);Release(ref code);throw new InvalidOperationException(message);}
        }
        Release(ref errors);return code;
    }
    public void Dispose()
    {
        if(context!=0)((delegate* unmanaged[Stdcall]<nint,void>)Method(context,110))(context); // ClearState
        background?.Dispose();foreground?.Dispose();background=foreground=null;
        if(target!=0)((delegate* unmanaged[Stdcall]<nint,nint,int>)Method(target,3))(target,0);
        if(composition!=0)((delegate* unmanaged[Stdcall]<nint,int>)Method(composition,3))(composition);
        Release(ref dirtyView);Release(ref dirtyTexture);Release(ref context1);
        Release(ref weatherView);Release(ref weatherTexture);
        Release(ref opacityEffect);
        Release(ref visual);Release(ref target);Release(ref composition);Release(ref rasterState);Release(ref blendState);Release(ref constants);Release(ref instances);Release(ref inputLayout);Release(ref pixelShader);Release(ref vertexShader);Release(ref renderTarget);Release(ref back);Release(ref swap);Release(ref context);Release(ref device);
    }
    const string Shader="""
    cbuffer Frame : register(b0) { float4 screen; float4 excluded[8]; };
    Texture2D image : register(t0);
    Texture2D<uint> dirtyMask : register(t1);
    Texture2D weatherImage : register(t2);
    struct Input { float4 rect:RECT; float4 color:COLOR; float3 mode:MODE; };
    struct Output { float4 position:SV_Position; float2 uv:TEXCOORD; nointerpolation float4 color:COLOR; nointerpolation float3 mode:MODE; };
    Output VS(Input input,uint id:SV_VertexID) {
        float2 points[6]={float2(0,0),float2(1,0),float2(0,1),float2(0,1),float2(1,0),float2(1,1)};
        Output o; o.uv=points[id]; float2 pixel=input.rect.xy+o.uv*input.rect.zw;
        o.position=float4(pixel*float2(2/screen.x,-2/screen.y)+float2(-1,1),0,1);o.color=input.color;o.mode=input.mode;return o;
    }
    float4 PS(Output input):SV_Target {
        if(screen.w>0 && dirtyMask.Load(int3(int2(input.position.xy*screen.w),0))==0)discard;
        [loop] for(int i=0;i<(int)screen.z;i++)if(input.position.x>=excluded[i].x&&input.position.y>=excluded[i].y&&input.position.x<excluded[i].z&&input.position.y<excluded[i].w)discard;
        if(input.mode.x>1.5) {
            float2 p=input.uv-.5; float s=sin(input.mode.z),c=cos(input.mode.z);
            float2 uv=float2(p.x*c+p.y*s,-p.x*s+p.y*c)+.5;
            if(any(uv<0)||any(uv>1))discard;
            int index=(int)input.mode.y;
            float2 origin=float2(index%4,index/4)*48;
            float2 tex=clamp(uv*48-.5,0,47); int2 pos=int2(tex); float2 f=frac(tex);
            int2 base=int2(origin)+pos; int2 hi=int2(origin)+min(pos+1,47);
            float4 a=lerp(weatherImage.Load(int3(base,0)),weatherImage.Load(int3(hi.x,base.y,0)),f.x);
            float4 b=lerp(weatherImage.Load(int3(base.x,hi.y,0)),weatherImage.Load(int3(hi,0)),f.x);
            return lerp(a,b,f.y)*input.color.a;
        }
        if(input.mode.x>0.5)return image.Load(int3(int2(input.position.xy)+int2(input.mode.yz), 0));
        if(input.mode.x<-.5) {
            float d=abs(input.uv.x-.5-(input.uv.y-.5)*input.mode.y);
            float a=(1-smoothstep(.11,.34,d))*input.color.a;
            return float4(input.color.rgb*a,a);
        }
        float radius=length(input.uv*2-1);float edge=max(fwidth(radius),0.001);float a=(1-smoothstep(1-edge*.5,1,radius))*input.color.a;
        return float4(input.color.rgb*a,a);
    }
    """;
    [StructLayout(LayoutKind.Sequential)] struct GpuParticle{public Vector4 Rect;public uint Color;public Vector3 Mode;}
    [StructLayout(LayoutKind.Sequential)] struct PresentParameters { public uint Count; public nint Rectangles, ScrollRect, ScrollOffset; }
    [StructLayout(LayoutKind.Sequential)] struct SwapDesc{public uint Width,Height,Format,Stereo,SampleCount,SampleQuality,Usage,Count,Scaling,SwapEffect,AlphaMode,Flags;}
    [StructLayout(LayoutKind.Sequential)] struct InputElement{public nint Name;public uint Index,Format,Slot,Offset,Class,Step;}
    [StructLayout(LayoutKind.Sequential)] struct BufferDesc{public uint Bytes,Usage,Bind,Cpu,Misc,Stride;}
    [StructLayout(LayoutKind.Sequential)] struct TextureDesc{public uint Width,Height,Mips,Array,Format,SampleCount,SampleQuality,Usage,Bind,Cpu,Misc;}
    [StructLayout(LayoutKind.Sequential)] struct Box{public uint Left,Top,Front,Right,Bottom,Back;}
    [StructLayout(LayoutKind.Sequential)] struct Mapped{public nint Data;public uint RowPitch,DepthPitch;}
    [StructLayout(LayoutKind.Sequential)] struct Viewport{public float X,Y,Width,Height,Min,Max;}
    [StructLayout(LayoutKind.Sequential)] struct RasterDesc{public uint Fill,Cull,CounterClockwise;public int Bias;public float Clamp,Slope;public uint DepthClip,Scissor,Multisample,Antialias;}
    [StructLayout(LayoutKind.Sequential)]
    struct AdapterDescription
    {
        public fixed char Description[128];
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct OutputDescription
    {
        public fixed char DeviceName[32];
        public Native.RECT DesktopBounds;
        public int AttachedToDesktop, Rotation;
        public nint Monitor;
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("d3d11.dll")]static extern int D3D11CreateDevice(nint adapter,int driver,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("dcomp.dll")]static extern int DCompositionCreateDevice(nint device,in Guid iid,out nint composition);
    [DllImport("d3dcompiler_47.dll",CharSet=CharSet.Ansi)]static extern int D3DCompile(nint data,nuint size,string name,nint defines,nint includes,string entry,string target,uint flags,uint effectFlags,out nint code,out nint errors);
}

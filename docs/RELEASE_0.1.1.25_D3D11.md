# Ryujinx Xbox UWP 0.1.1.25 — D3D11 GAL

Esta release integra a entrega `ryujinx_d3d11_80pct_all_files.zip` ao port UWP sem substituir as correções já validadas do projeto.

## Alterações principais

- Remapeamento do `ResourceLayout`/descriptor sets do Ryujinx para os registradores CBV, SRV, UAV e sampler do D3D11 por estágio.
- Tradução SPIR-V para HLSL 5.0 com opções de compatibilidade, semânticas de matriz, inicialização definida e bindings explícitos.
- Uniform buffers por `BufferRange`, respeitando offset, tamanho, alinhamento de 16 bytes e limite de 64 KiB do D3D11.
- Texture-buffer com SRV/UAV tipados por formato e intervalo.
- Texturas 1D, 3D, arrays, cubemap/cubemap-array, views por mip/camada e MSAA color resolve/readback.
- Upload/cópia parcial de formatos comprimidos compatíveis e alinhados por bloco.
- Instancing, indirect draw, stream-output com recurso espelho, fragment/compute UAV, queries e counters.
- Shadows, staging e resolve de textura criados sob demanda para reduzir pressão sobre os 5 GB do Xbox.
- Sync/counter queries com detecção de device-lost e timeout de 30 segundos, evitando espera infinita.
- Host UWP D3D11, SPIRV-Cross e CoreHost NativeAOT incluídos no pacote.

## Validação realizada

- Build do renderer e CoreHost: sucesso.
- Publicação NativeAOT win-x64: sucesso.
- Teste D3D11 local: SPIR-V → HLSL → DXBC, criação de device FL 11.0, buffers, uniform range, RGBA/BC1, 1D, 3D, cubemap, MSAA e texture-buffer tipado.
- MSIX x64 gerado e assinatura `CN=Ryujinx Xbox Dev` validada.

## Limites que exigem validação no Xbox

ETC2/ASTC continuam pelo fallback de descompressão/transcodificação do Ryujinx, pois D3D11 não possui formatos DXGI equivalentes. Depth/stencil MSAA pode ser usado como target, mas readback/resolve de depth ainda exige shader específico. Device-lost é detectado e interrompido com segurança; recriar todos os recursos de uma sessão ativa requer reiniciar a sessão do jogo. Compatibilidade visual e desempenho só podem ser confirmados com jogos e capturas em hardware Xbox real.

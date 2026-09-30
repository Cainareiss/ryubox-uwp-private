# Relatório da adaptação D3D12 inspirada no Eden UWP

Atualizado em 16/09/2026. Este documento separa o que foi implementado e
testado no Ryubox do que foi apenas investigado no MSIX do Eden.

## O que foi adaptado

### Subalocação nativa de memória D3D12

O backend passou a usar heaps D3D12 de 64 MiB e `CreatePlacedResource` para
recursos compatíveis de até 32 MiB em `ResourceHeapTier 2`. A alocação respeita
alinhamento retornado por `GetResourceAllocationInfo`, mantém o heap vivo junto
do recurso, retém recursos até a fence correspondente e emite barreira de
aliasing na primeira utilização. Recursos grandes, Tier 1 e casos especiais
continuam usando `CreateCommittedResource`, ainda dentro do backend D3D12.

Foi adicionada uma proteção para a Intel HD Graphics 620/driver antigo: o
subalocador colocado é desativado automaticamente nesse fabricante, pois o
teste reproduziu `0x887A0006` na segunda sessão; o caminho continua sendo
D3D12 nativo, apenas com recursos committed. AMD/Xbox não passa por essa
proteção. Intel atualizado pode reativar o pool com
`RYUBOX_D3D12_ENABLE_INTEL_POOL=1` após validar o driver.

### Descritores e reciclagem por fence

O backend usa 16.384 descritores CBV/SRV/UAV e 512 samplers por fatia, com três
fatias associadas às fences. Os contadores gerenciados foram alinhados aos
limites nativos; a reciclagem ocorre somente quando a fence permite reutilizar
a fatia. Isso elimina o limite artificial anterior de 1.024 views por frame.

Isso não é o `DescriptorRing` paginado completo do Eden: é uma implementação
D3D12 nativa de fatias fixas recicladas por fence. Um ring paginado dinâmico
continua sendo uma melhoria futura.

### Frontend ImGui e CoreHost no mesmo contexto D3D12

O frontend UWP completo agora inicializa o shell ImGui em D3D12 e entrega um
lease do mesmo contexto ao CoreHost. O CoreHost devolve o contexto antes do
frontend voltar a desenhar; resize/DPI são adiados durante o lease; ações e
input ficam bloqueados enquanto o jogo é dono da superfície; o contador de FPS
conta apresentações concluídas. D3D11 permanece disponível como backend
separado e não é usado no fluxo D3D12.

### Correções de robustez associadas

- shutdown/recovery não deixa a UI presa em um contexto nativo já destruído;
- cleanup ocorre depois da conclusão da thread GPU e da fence;
- o caminho D3D12 Intel problemático foi isolado e passou na regressão de três
  sessões;
- o pacote verifica hashes dos binários nativos antes de ser entregue;
- o wrapper DXIL recebeu o mapeamento correto dos estágios Mesa.

## O que foi encontrado no Eden, mas não foi ativado

O MSIX `eden-uwp_0.29.msix` contém `spirv_to_dxil.dll`, `dxil.dll` e os exports:

- `spirv_to_dxil`;
- `spirv_to_dxil_free`;
- `spirv_to_dxil_get_version`;
- `spirv_to_dxil_pipeline`.

O último export não está descrito pelo header disponível no pacote do Ryubox.
Além disso, ativar apenas a tradução SPIR-V→DXIL não resolveria a integração:
seriam necessários o contrato de pipeline, reflexão de bindings DXIL,
normalização de register spaces, buffers de runtime/sysvals, validação DXIL e
criação/teste dos PSOs com todos os estágios.

Por isso o gate de DXIL permanece fechado. O caminho de produção desta versão
é `SPIR-V → SPIRV-Cross → HLSL 5.1 → DXBC`, compilado e executado por D3D12;
não há fallback para D3D11 nesse caminho. Copiar as DLLs do Eden sem cumprir
essas condições poderia introduzir shaders inválidos e crashes.

## Evidência obtida

- Regressão nativa/managed D3D12 com WARP e GPU física: PASS.
- Três sessões consecutivas na Intel após a proteção de alocação: PASS;
  pixels GAL, handoff da thread GPU, cleanup, retorno ao menu e overlay FPS.
- CoreHost usando o lease do frontend: PASS; captura real do logo/animação do
  Sonic Mania e retorno ao menu: PASS.
- A sessão física do Sonic no notebook Intel apresentou variação aproximada de
  15–25 apresentações/s durante a abertura; não foi certificada em 60 FPS.
- Não houve medição de FPS no Xbox nesta rodada. O PC Intel não é um proxy
  confiável do desempenho do Xbox Series S.

Além das evidências acima, a regressão final foi repetida usando exatamente a
`Ryujinx.Uwp.Native.dll` incorporada ao MSIX: três sessões passaram com pixels
GAL, handoff da thread GPU, cleanup e retorno ao frontend.

## Expectativa no Xbox Series S

O Xbox deve usar o caminho D3D12 nativo com subalocação colocada ativa, pois a
proteção criada é específica para a família Intel problemática. A expectativa
é menor pressão de commits, menos falhas de orçamento/descritores e menor
stutter de alocação em cenas densas. Isso não permite prometer 60 FPS: o
framerate dependerá da tradução dos shaders, CPU, compatibilidade do jogo,
VSync e carga da cena. O primeiro teste de desempenho deve ser feito no Xbox
com o overlay FPS habilitado e uma cena repetível.

## Pacote gerado

MSIX assinado:

`E:\ports\ryujinx uwp\artifacts\xbox-d3d12-120-final\Release\AppPackages\Ryujinx.Uwp.App_0.2.0.120_x64_Test\Ryujinx.Uwp.App_0.2.0.120_x64.msix`

Certificado correspondente:

`E:\ports\ryujinx uwp\artifacts\xbox-d3d12-120-final\Release\AppPackages\Ryujinx.Uwp.App_0.2.0.120_x64_Test\RyujinxXboxDevCodeSign.cer`

Hashes verificados no MSIX:

- CoreHost: `90A10E09FD775AEFFB0C0F030A171A7CF150A4B8DF82E17F76D2C2A43CD66DBD`;
- Native: `B99E3C8BB4518D48A0EFB9AEBC010B64E0AB543A5DBBB294EDDBA960FC873C2A`;
- SPIRV-Cross UWP: `82873DD808AA7C10F3D105D11FB47BB5B9A54D244D5DB42DCB3BB1BC015DBF76`.

O pacote não foi instalado no Xbox nesta etapa.

# Z-A — investigação do acesso guest 0xD0 (27/09/2026)

Estado: **crash reproduzido; causa da inicialização incorreta ainda não
confirmada**. Este documento não certifica boot nem conclusão de P1.

## Reprodução sem entrada

Runtime retail da rodada anterior, CoreHost D3D12 direto, janela oculta,
sem AutoPressA/teclado e com cota host de 5 GiB:

`artifacts/direct-d3d12-za-retail-20260927/noinput-20260927-214202`.

Início UTC 2026-09-28 00:42:03; encerramento UTC 00:51:49. O guest repetiu
o acesso inválido a 0xD0, agora na JobThread01. PC reportado 0x08770B40,
LR 0x0AF99AC4 e X0 zero, iguais aos da reprodução com entrada automática.
Portanto, a entrada automática não é necessária para reproduzir a falha.

Foi salvo `pokemon-frame-8.png` aceitando conteúdo uniforme exclusivamente
para diagnóstico. Inspeção visual: tela preta. O texto automático do log
"Captured visible D3D12 frame" não significa que exista imagem útil do jogo.

## Localização da falha

A inspeção local do executável do usuário identificou:

- O chamador lê um sinalizador de 32 bits em 0x0E413880.
- Se o sinalizador é zero, seleciona X0=0; caso contrário, X0=0x0E413780.
- A leitura que falha é `X0 + 0xD0`, em 0x08770B4C. O PC 0x08770B40
  do relatório é a entrada da tradução, não a instrução exata da leitura.
- Há código de publicação usando LDXR WZR/STLXR para escrever 1 no
  sinalizador e código de destruição que escreve zero.
- A inicialização passa pelo ponteiro global em 0x0C1CA690. No primeiro
  registro dinâmico, ele aponta corretamente para 0x0E413780.

Esses endereços são específicos do binário base v1.0.0, main carregado em
0x08505000. Não foram incorporados como hacks no emulador. Falta distinguir
inicialização não executada, objeto destruído prematuramente e corrupção.
Não há evidência suficiente para atribuir este crash ao driver D3D12.

## Regressões executáveis

`src/eng/JitCacheRegression`: 210 verificações aprovadas, incluindo os testes
existentes de reclaim/fault e os novos casos de publicação atômica:

- dispatch nativo e gerenciado;
- PTE normal e marcado para tracking;
- sinalizador inicialmente zero ou sete;
- publicação/consumo no mesmo fluxo ou separados por BL/RET;
- LDXR com destino WZR continua estabelecendo a reserva exclusiva;
- preservação da seleção condicional e liberação do cache ao terminar.

O fixture de tracking sinaliza a página, mas não reproduz todo o cache GPU
ou todos os interleavings do kernel. PASS não exclui bugs de concorrência
no jogo real.

Comando, na raiz Git `src`:

```powershell
dotnet run --project eng/JitCacheRegression/JitCacheRegression.csproj -c Release --no-restore
```

Saída: `artifacts/za-exclusive-publication-tests.log` na raiz externa.

## Diagnóstico opt-in

`ARMeilleure/Diagnostics/GuestDispatchProbe.cs` permite observação limitada
na aquisição de traduções por `RYUJINX_UWP_GUEST_PROBE`:

```text
PC_HEX[,PC_HEX...]|WORD_ADDRESS_HEX[,WORD_ADDRESS_HEX...]
```

Máximo oito PCs, oito palavras alinhadas de 64 bits e oito amostras por PC.
Desativado por padrão, sem alterações de registradores/memória guest.
Endereços não mapeados e erros de leitura são registrados distintamente.
Amostras de threads diferentes não são um snapshot atômico. A instrumentação
observa entradas de tradução, não cada instrução nem cada escrita interna;
ausência de registro de uma função não prova que ela nunca executou como
bloco interno de outra tradução.

Runtime diagnóstico isolado, compilação AOT concluída (com os avisos
NU1900/ILC já existentes):
`artifacts/direct-d3d12-za-probe-20260927`.

SHA-256 CoreHost:
`E8CED550E192E3BC3BC2A2AD3701A10BDCFD01A357CA42A37CD664647271F313`.

Launcher reproduzível: `artifacts/za-probe-retest-20260927.ps1`.
Não foi gerado MSIX, alterado o orçamento ou aplicado retorno zero para
contornar acesso guest inválido.

### Resultado do primeiro probe

`artifacts/direct-d3d12-za-probe-20260927/probe-20260927-220703`:
crash após ~6min36s do guest, JobThread01, mesma instrução de leitura.
Na entrada da função, X0=0; o ponteiro em 0x0C1CA690 contém 0x0E413780,
mas a primeira palavra do objeto, a palavra no offset 0xD0 e o sinalizador
no offset 0x100 estão todos zerados. Nenhum registro dos pontos específicos
de publicação/destruição selecionados foi emitido. Isso direciona a próxima
reprodução aos pré-requisitos da inicialização, sem ainda provar a causa.

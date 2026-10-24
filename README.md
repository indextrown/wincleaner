# WinCleaner

Windows 10/11용 디스크 청소 · 용량 분석 · 시스템 최적화 도구입니다. 오래 써서 느려진 PC에서 공간을 확보하고 설정을 정리하는 것이 목표이며, **모든 변경은 미리보기와 확인을 거치고 기록으로 남겨 되돌릴 수 있게** 만들었습니다.

- C# / .NET 10 / WPF
- 대상 OS: Windows 10 22H2, Windows 11 (x64)
- 자세한 설계와 진행 상황: [docs/spec.md](docs/spec.md)

## 주요 기능

| 메뉴 | 하는 일 |
|---|---|
| 대시보드 | 드라이브별 사용량(여유 15% 미만이면 경고), 메모리 사용량, 권장 작업 |
| 디스크 청소 | 임시 파일, Windows Update 다운로드 파일, 메모리 덤프, 오류 보고서, 썸네일 캐시, 브라우저(Chrome·Edge·Firefox) 캐시, 휴지통을 스캔하고 미리보기로 확인한 뒤 고른 항목만 정리 |
| 용량 분석 | 드라이브 전체의 폴더별 크기와 가장 큰 파일 200개를 보여 줌. 큰 시스템 파일(hiberfil.sys, WinSxS 등)은 무엇이고 지워도 되는지 설명 |
| 최적화 | 시작 프로그램 켜고 끄기, 전원 관리 옵션, 시각 효과 줄이기, 최대 절전 끄기, 드라이브 최적화(SSD는 TRIM, HDD는 조각 모음)와 디스크 건강 상태 |
| 기록 | WinCleaner가 한 일 전체 목록, 설정 변경 되돌리기, 시스템 복원 지점 만들기 |

### 안전 원칙

- 스캔은 아무것도 지우지 않습니다. 정리는 미리보기에서 고른 항목만 합니다.
- 시스템 폴더와 문서·바탕 화면 같은 사용자 폴더는 규칙이 잘못돼도 통째로 지울 수 없게 막혀 있습니다.
- 링크·정션은 따라가지 않아, 정리 대상 폴더 밖의 파일이 지워지지 않습니다.
- 위험도 "주의" 항목(휴지통, 최대 절전 끄기)은 기본으로 선택되지 않으며, 설정 변경 전에는 시스템 복원 지점을 만듭니다.
- 설정 변경은 바꾸기 전 값을 기록해 두었다가 그대로 되돌립니다.
- 레지스트리 클리너, Prefetch 삭제, Defender·Windows Update 끄기, 자동 RAM 부스터는 일부러 넣지 않았습니다. 이유는 [사양서 3.5](docs/spec.md)에 있습니다.

## 실행 방법

### 1. .NET 10 SDK 설치

이미 설치되어 있다면 건너뜁니다. 터미널에서 `dotnet --list-sdks`를 실행해 `10.0`으로 시작하는 줄이 있으면 됩니다.

- **winget이 있으면** (관리자 권한 필요):

  ```powershell
  winget install Microsoft.DotNet.SDK.10
  ```

- **winget이 없으면** [.NET 10 다운로드 페이지](https://dotnet.microsoft.com/download/dotnet/10.0)에서 Windows x64용 SDK 설치 파일을 받아 실행합니다.

설치한 뒤에는 **새 터미널을 열어야** `dotnet` 명령이 인식됩니다.

### 2. 받아서 빌드하기

```powershell
git clone https://github.com/INDEXTROWN/wincleaner.git
```

```powershell
cd wincleaner
```

```powershell
dotnet build WinCleaner.slnx
```

### 3. 실행하기

아래 파일을 더블클릭합니다.

```
src\WinCleaner.App\bin\Debug\net10.0-windows\WinCleaner.exe
```

실행하면 "이 앱이 디바이스를 변경할 수 있도록 허용하시겠어요?"라는 UAC 창이 뜹니다. **예**를 누르세요. 시스템 영역 정리와 최적화에는 관리자 권한이 필요합니다.

터미널에서 실행하려면 다음 명령을 씁니다. UAC 창이 뜹니다.

```powershell
Start-Process .\src\WinCleaner.App\bin\Debug\net10.0-windows\WinCleaner.exe
```

> `dotnet run`은 쓰지 마세요. 앱이 관리자 권한을 요구하기 때문에, 관리자 터미널이 아니면 "요청한 작업을 수행하려면 권한 상승이 필요합니다" 오류가 납니다.

#### 개발 중에 UAC 없이 실행하기

화면만 확인할 때는 일반 권한으로 실행할 수 있습니다. 이때 관리자 권한이 필요한 항목은 스캔·조회만 되고 정리·변경 버튼은 막힙니다.

```powershell
$env:__COMPAT_LAYER = 'RunAsInvoker'; Start-Process .\src\WinCleaner.App\bin\Debug\net10.0-windows\WinCleaner.exe
```

### 4. 테스트

```powershell
dotnet test WinCleaner.slnx
```

테스트는 임시 폴더 안에 만든 가짜 파일과 테스트용 대역으로 동작합니다. 실제 PC의 파일이나 설정은 읽기만 하고 바꾸지 않습니다.

## 기록 파일 위치

| 파일 | 위치 |
|---|---|
| 정리 기록 (지운 파일 목록) | `%LOCALAPPDATA%\WinCleaner\logs\cleanup-날짜-시각.log` |
| 변경 기록 (되돌리기용) | `%LOCALAPPDATA%\WinCleaner\journal.json` |

## 프로젝트 구조

```
├─ docs\spec.md               # 사양서 (설계, 안전 원칙, 단계별 진행 상황)
├─ rules\cleanup-rules.json   # 디스크 청소 규칙 (빌드할 때 프로그램 안에 포함)
├─ src\
│  ├─ WinCleaner.App\         # WPF 화면 (MVVM)
│  ├─ WinCleaner.Core\        # 공통 인터페이스, 플랫폼 정보
│  ├─ WinCleaner.Cleaning\    # 청소 규칙 엔진, 휴지통, 용량 분석
│  ├─ WinCleaner.Memory\      # 메모리 현황
│  ├─ WinCleaner.Optimizer\   # 최적화 항목, 시작 프로그램, 드라이브
│  └─ WinCleaner.Safety\      # 변경 기록, 시스템 복원 지점
└─ tests\WinCleaner.Tests\
```

## 알려진 제한

- 관리자 권한이 필요한 동작(복원 지점 만들기, 최대 절전 끄기, 모든 사용자 시작 프로그램 바꾸기, 드라이브 최적화)은 자동 테스트에서 대역으로만 검증했습니다.
- 작업 스케줄러에 등록된 시작 작업은 아직 다루지 않습니다.
- 메모리 메뉴는 준비 중입니다.
- 일부 그래픽 환경(원격 접속용 가상 디스플레이 등)에서 화면이 하얗게 나오는 문제 때문에 소프트웨어 렌더링을 씁니다.

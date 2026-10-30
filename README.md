# Madi

Madi는 프로젝트 폴더를 작업공간으로 사용하는 Windows용 GUI 코딩 에이전트입니다. 대화로 코드 탐색·수정·테스트를 요청할 수 있으며, 작업공간별 대화와 여러 탭, 모델 선택, 도구 실행 내역을 지원합니다.

작업 단위로 짧은 세션을 반복적으로 사용하는 개인 사용 패턴에 맞춰 제작되었습니다.

## 요구 사항

- Windows 10 버전 1809 이상, x64
- 소스에서 빌드할 경우 .NET 10 SDK 및 PowerShell
- 모델을 사용하기 위한 네트워크 연결과 해당 모델의 인증 정보

## 시작하기

1. 저장소 루트에서 PowerShell로 GUI를 빌드합니다.

   ```powershell
   .\build-gui.ps1
   ```

2. `artifacts/publish/madi-gui/release/win-x64/Madi.GUI.exe`를 실행합니다. 배포 빌드는 자체 포함 단일 EXE이며, 실행 시 포함된 파일을 임시 디렉터리에 추출합니다.
3. 시작 화면에서 프로젝트 폴더를 작업공간으로 선택합니다. 기본 모델은 OpenAI OAuth 방식이므로, 화면에서 로그인이 필요하다고 표시되면 로그인한 뒤 대화를 시작합니다.

빌드 구성을 변경하려면 `.\build-gui.ps1 -Configuration Debug`를 사용합니다. 스크립트는 해당 구성의 기존 게시 폴더를 지우고 다시 게시합니다.

## 모델과 데이터 설정

처음 실행하면 `%LOCALAPPDATA%\madi`에 `models.json`, `SYSTEM.md` 등이 생성됩니다. `MADI_HOME` 환경 변수를 설정하면 모델 목록, 시스템 프롬프트, 대화 데이터 등의 저장 위치를 변경할 수 있습니다. 단, OAuth 인증 정보는 별도로 `%LOCALAPPDATA%\madi\auth.json`에 저장됩니다.

기본 `models.json`은 `openai-codex` API의 모델들을 제공합니다. OpenAI 호환 Responses API를 API 키로 사용하려면 앱을 종료한 뒤 `models.json`의 공급자 항목을 아래와 같이 설정하고 다시 실행하세요. `baseUrl`과 `name`은 실제 서비스의 값으로 바꿔야 합니다.

```json
[
  {
    "provider": "MyProvider",
    "api": "openai-responses",
    "baseUrl": "https://example.com/v1/",
    "apiKey": "YOUR_API_KEY",
    "models": [
      {
        "name": "model-name",
        "supportsReasoning": false,
        "maxContextTokens": 128000,
        "maxOutputTokens": 4096
      }
    ]
  }
]
```

`models.json`에 입력한 API 키는 파일에 저장되므로 파일을 공유하거나 버전 관리에 추가하지 마세요. 작업공간 루트의 `AGENTS.md`는 프로젝트별 지침으로 읽습니다. `SYSTEM.md`는 기본 시스템 프롬프트를 바꿀 때 편집할 수 있습니다.

## 주요 기능

- 작업공간별 대화 관리 및 여러 작업공간 탭
- Work로 대화를 묶고 Work 프롬프트를 설정
- 모델·추론 수준 선택, OAuth 로그인/로그아웃
- 파일 읽기·쓰기·수정, Bash 명령 실행, 파일 및 내용 검색
- 대화 타임라인에서 도구 실행 내역 확인 및 Markdown 출력 보기

처음 실행 시 Windows x64용 `fd`와 `rg`가 없으면 최신 GitHub 릴리스에서 내려받아 Madi 데이터 폴더의 `bin` 디렉터리에 설치를 시도합니다. 설치에 실패해도 앱 자체는 실행됩니다.

## 개발 및 테스트

```powershell
# 전체 솔루션 테스트
dotnet test .\madi.sln

# GUI 배포 빌드
.\build-gui.ps1
```

프로젝트 구성과 의존성은 [ARCHITECTURE.md](ARCHITECTURE.md)를 참고하세요.

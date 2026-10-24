namespace WinCleaner.Core;

/// <summary>
/// 작업의 위험도 (사양서 3장). 낮음만 기본으로 선택되고, 높음은 실행 전에 한 번 더 확인한다.
/// </summary>
public enum RiskLevel
{
    /// <summary>읽기 전용. 아무것도 바꾸지 않는다.</summary>
    None,

    /// <summary>지워도 앱이 다시 만드는 파일 등.</summary>
    Low,

    /// <summary>되돌릴 수 없거나 사용자 데이터가 섞일 수 있는 항목.</summary>
    Medium,

    /// <summary>시스템 동작이 바뀌는 항목.</summary>
    High,
}

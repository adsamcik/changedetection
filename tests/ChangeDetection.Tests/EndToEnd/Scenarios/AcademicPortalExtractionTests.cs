using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// E2E tests for academic portal extraction scenarios.
/// Tests LLM ability to extract info from course registration, admissions, and grade portals.
/// </summary>
public class AcademicPortalExtractionTests : ExtractionTestBase
{
    #region Test HTML Fixtures

    private const string CourseRegistrationHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Course Registration | State University</title></head>
        <body>
            <header class="portal-header">
                <span class="university-name" data-university="State University">State University</span>
                <span class="user-info" data-user="John Doe (ID: 12345678)">John Doe (ID: 12345678)</span>
            </header>
            <main class="main-content" data-registration>
                <h1 class="page-title" data-title="Course Registration">Course Registration</h1>
                <select class="term-selector" data-term="Spring 2025">
                    <option selected>Spring 2025</option>
                </select>
                <div class="registration-status" data-status>
                    <div class="status-title">Registration Open</div>
                    <div class="status-text">Priority registration ends January 20, 2025</div>
                </div>
                <div class="course-list" data-courses>
                    <div class="course-card" data-course="CS301">
                        <div class="course-code" data-code="CS 301">CS 301</div>
                        <div class="course-title" data-name="Data Structures and Algorithms">Data Structures and Algorithms</div>
                        <span class="course-credits" data-credits="4">4 Credits</span>
                        <div class="section-row" data-section="001">
                            <span class="section-schedule" data-schedule="MWF 9:00-9:50 AM">MWF 9:00-9:50 AM</span>
                            <span class="section-instructor" data-instructor="Dr. Smith">Dr. Smith</span>
                            <span class="section-seats" data-seats="3/30">3 of 30 seats</span>
                        </div>
                    </div>
                    <div class="course-card" data-course="MATH201">
                        <div class="course-code" data-code="MATH 201">MATH 201</div>
                        <div class="course-title" data-name="Linear Algebra">Linear Algebra</div>
                        <span class="course-credits" data-credits="3">3 Credits</span>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string AdmissionsPortalHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Application Status | Pacific University</title></head>
        <body>
            <header class="admissions-header">
                <div class="logo" data-university="Pacific University">Pacific University</div>
                <div class="applicant-info" data-applicant="Jane Smith | APP-2025-78432">Jane Smith | APP-2025-78432</div>
            </header>
            <main class="status-container" data-application>
                <div class="welcome-card">
                    <h1 class="welcome-title">Welcome, Jane!</h1>
                </div>
                <div class="decision-card" data-decision>
                    <div class="decision-header">
                        <div class="decision-icon">🎉</div>
                        <div class="decision-title" data-status="Admitted">Congratulations! You've Been Admitted!</div>
                        <div class="decision-date" data-date="January 15, 2025">Decision Date: January 15, 2025</div>
                    </div>
                    <div class="decision-body">
                        <p class="decision-message">
                            We are thrilled to offer you admission to Pacific University for Fall 2025.
                        </p>
                        <div class="next-steps" data-steps>
                            <h3>Next Steps</h3>
                            <div class="step-item" data-step="1">
                                <div class="step-action">Submit enrollment deposit ($500)</div>
                                <div class="step-deadline" data-deadline="May 1, 2025">Due: May 1, 2025</div>
                            </div>
                            <div class="step-item" data-step="2">
                                <div class="step-action">Complete housing application</div>
                                <div class="step-deadline" data-deadline="March 15, 2025">Due: March 15, 2025</div>
                            </div>
                        </div>
                    </div>
                </div>
            </main>
        </body>
        </html>
        """;

    private const string GradePortalHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Grade Report | Student Portal</title></head>
        <body>
            <header class="portal-header">
                <span class="portal-name">Student Academic Portal</span>
                <span class="student-info" data-student="Michael Chen | ID: 20231456">Michael Chen | ID: 20231456</span>
            </header>
            <main class="main-container" data-grades>
                <h1 class="page-title">Grade Report</h1>
                <span class="term-badge" data-term="Fall 2024">Fall 2024</span>
                <div class="gpa-cards" data-summary>
                    <div class="gpa-card">
                        <div class="gpa-value" data-term-gpa="3.67">3.67</div>
                        <div class="gpa-label">Term GPA</div>
                    </div>
                    <div class="gpa-card">
                        <div class="gpa-value" data-cumulative-gpa="3.54">3.54</div>
                        <div class="gpa-label">Cumulative GPA</div>
                    </div>
                    <div class="gpa-card">
                        <div class="gpa-value" data-credits="92">92</div>
                        <div class="gpa-label">Total Credits</div>
                    </div>
                </div>
                <div class="grades-card" data-courses>
                    <table class="grades-table">
                        <tbody>
                            <tr data-course="CS401">
                                <td>
                                    <div class="course-code" data-code="CS 401">CS 401</div>
                                    <div class="course-name" data-name="Machine Learning">Machine Learning</div>
                                </td>
                                <td data-credits="4">4</td>
                                <td><span class="grade-badge" data-grade="A">A</span></td>
                            </tr>
                            <tr data-course="MATH301">
                                <td>
                                    <div class="course-code" data-code="MATH 301">MATH 301</div>
                                    <div class="course-name" data-name="Probability">Probability and Statistics</div>
                                </td>
                                <td data-credits="3">3</td>
                                <td><span class="grade-badge" data-grade="B+">B+</span></td>
                            </tr>
                        </tbody>
                    </table>
                </div>
            </main>
        </body>
        </html>
        """;

    #endregion

    #region E2E Tests (LLM Cached)

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAcademic_CourseRegistration_ExtractsCourseInfo()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(CourseRegistrationHtml, new TestExtractionSchema
        {
            Name = "CourseRegistration",
            Description = "Extract course registration information",
            Fields =
            [
                new TestSchemaField { Name = "university", Type = "string", Description = "University name" },
                new TestSchemaField { Name = "term", Type = "string", Description = "Academic term" },
                new TestSchemaField { Name = "courses", Type = "array", Description = "Available courses" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var term = result.GetString("term");
        term.ShouldContain("Spring", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAcademic_Admissions_ExtractsDecisionStatus()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(AdmissionsPortalHtml, new TestExtractionSchema
        {
            Name = "AdmissionsStatus",
            Description = "Extract admissions decision and status",
            Fields =
            [
                new TestSchemaField { Name = "applicantName", Type = "string", Description = "Applicant name" },
                new TestSchemaField { Name = "decision", Type = "string", Description = "Admission decision" },
                new TestSchemaField { Name = "decisionDate", Type = "string", Description = "Decision date" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var decision = result.GetString("decision");
        decision.ShouldContain("Admit", Case.Insensitive);
    }

    [Test]
    [Category("LlmCached")]
    public async Task ExtractAcademic_Grades_ExtractsGpaAndCourses()
    {
        var llmProvider = await CreateRealLlmProvider();
        var service = new ObjectExtractionTestService(llmProvider);

        var result = await service.ExtractStructuredDataAsync(GradePortalHtml, new TestExtractionSchema
        {
            Name = "GradeReport",
            Description = "Extract student grade report",
            Fields =
            [
                new TestSchemaField { Name = "studentName", Type = "string", Description = "Student name" },
                new TestSchemaField { Name = "term", Type = "string", Description = "Academic term" },
                new TestSchemaField { Name = "termGpa", Type = "number", Description = "Term GPA" },
                new TestSchemaField { Name = "cumulativeGpa", Type = "number", Description = "Cumulative GPA" }
            ]
        });

        TestContext.Current?.OutputWriter?.WriteLine($"Extraction result: {result.Data}");

        result.ShouldNotBeNull();
        AssertExtractionSuccessOrSkipOnCacheMiss(result);

        var term = result.GetString("term");
        term.ShouldContain("Fall", Case.Insensitive);
    }

    #endregion
}


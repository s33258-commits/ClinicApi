using ClinicApi.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AppointmentsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // GET /api/appointments
    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var result = new List<AppointmentListDto>();
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + ' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM Appointments a
            JOIN Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
        ", connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar).Value = (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = (int)reader["IdAppointment"],
                AppointmentDate = (DateTime)reader["AppointmentDate"],
                Status = reader["Status"].ToString()!,
                Reason = reader["Reason"].ToString()!,
                PatientFullName = reader["PatientFullName"].ToString()!,
                PatientEmail = reader["PatientEmail"].ToString()!
            });
        }

        return Ok(result);
    }

    // GET /api/appointments/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAppointment(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(@"
            SELECT a.*, 
                   p.FirstName, p.LastName, p.Email, p.PhoneNumber,
                   d.FirstName AS DoctorFirstName, d.LastName AS DoctorLastName, d.LicenseNumber,
                   s.Name AS SpecializationName
            FROM Appointments a
            JOIN Patients p ON p.IdPatient = a.IdPatient
            JOIN Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @Id;
        ", connection);

        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found" });

        var dto = new AppointmentDetailsDto
        {
            IdAppointment = (int)reader["IdAppointment"],
            AppointmentDate = (DateTime)reader["AppointmentDate"],
            Status = reader["Status"].ToString()!,
            Reason = reader["Reason"].ToString()!,
            InternalNotes = reader["InternalNotes"] as string,
            CreatedAt = (DateTime)reader["CreatedAt"],

            IdPatient = (int)reader["IdPatient"],
            PatientFullName = reader["FirstName"] + " " + reader["LastName"],
            PatientEmail = reader["Email"].ToString()!,
            PatientPhoneNumber = reader["PhoneNumber"].ToString()!,

            IdDoctor = (int)reader["IdDoctor"],
            DoctorFullName = reader["DoctorFirstName"] + " " + reader["DoctorLastName"],
            DoctorLicenseNumber = reader["LicenseNumber"].ToString()!,
            SpecializationName = reader["SpecializationName"].ToString()!
        };

        return Ok(dto);
    }

    // POST /api/appointments
    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate < DateTime.Now)
            return BadRequest(new ErrorResponseDto { Message = "Date cannot be in the past" });

        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // check conflict
        await using var checkCommand = new SqlCommand(@"
            SELECT COUNT(*) FROM Appointments
            WHERE IdDoctor = @Doctor AND AppointmentDate = @Date;
        ", connection);

        checkCommand.Parameters.Add("@Doctor", SqlDbType.Int).Value = request.IdDoctor;
        checkCommand.Parameters.Add("@Date", SqlDbType.DateTime).Value = request.AppointmentDate;

        var exists = (int)await checkCommand.ExecuteScalarAsync();

        if (exists > 0)
            return Conflict(new ErrorResponseDto { Message = "Doctor already has appointment at this time" });

        // insert
        await using var command = new SqlCommand(@"
            INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@Patient, @Doctor, @Date, 'Scheduled', @Reason);
        ", connection);

        command.Parameters.Add("@Patient", SqlDbType.Int).Value = request.IdPatient;
        command.Parameters.Add("@Doctor", SqlDbType.Int).Value = request.IdDoctor;
        command.Parameters.Add("@Date", SqlDbType.DateTime).Value = request.AppointmentDate;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar).Value = request.Reason;

        await command.ExecuteNonQueryAsync();

        return StatusCode(201);
    }
    // PUT /api/appointments/{id}
[HttpPut("{id}")]
public async Task<IActionResult> UpdateAppointment(int id, UpdateAppointmentRequestDto request)
{
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
        return BadRequest(new ErrorResponseDto { Message = "Reason is required and max 250 characters" });

    if (request.Status != "Scheduled" && request.Status != "Completed" && request.Status != "Cancelled")
        return BadRequest(new ErrorResponseDto { Message = "Invalid status" });

    var connectionString = _configuration.GetConnectionString("DefaultConnection");

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    await using var checkAppointmentCommand = new SqlCommand(@"
        SELECT Status, AppointmentDate
        FROM dbo.Appointments
        WHERE IdAppointment = @Id;
    ", connection);

    checkAppointmentCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;

    string? oldStatus = null;
    DateTime oldDate = default;

    await using (var reader = await checkAppointmentCommand.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
            return NotFound(new ErrorResponseDto { Message = "Appointment not found" });

        oldStatus = reader["Status"].ToString();
        oldDate = (DateTime)reader["AppointmentDate"];
    }

    await using var checkPatientCommand = new SqlCommand(@"
        SELECT COUNT(*)
        FROM dbo.Patients
        WHERE IdPatient = @IdPatient AND IsActive = 1;
    ", connection);

    checkPatientCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;

    var patientExists = (int)await checkPatientCommand.ExecuteScalarAsync();

    if (patientExists == 0)
        return BadRequest(new ErrorResponseDto { Message = "Patient does not exist or is inactive" });

    await using var checkDoctorCommand = new SqlCommand(@"
        SELECT COUNT(*)
        FROM dbo.Doctors
        WHERE IdDoctor = @IdDoctor AND IsActive = 1;
    ", connection);

    checkDoctorCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;

    var doctorExists = (int)await checkDoctorCommand.ExecuteScalarAsync();

    if (doctorExists == 0)
        return BadRequest(new ErrorResponseDto { Message = "Doctor does not exist or is inactive" });

    if (oldStatus == "Completed" && oldDate != request.AppointmentDate)
        return Conflict(new ErrorResponseDto { Message = "Cannot change date of completed appointment" });

    await using var conflictCommand = new SqlCommand(@"
        SELECT COUNT(*)
        FROM dbo.Appointments
        WHERE IdDoctor = @IdDoctor
          AND AppointmentDate = @AppointmentDate
          AND IdAppointment <> @IdAppointment;
    ", connection);

    conflictCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
    conflictCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
    conflictCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

    var conflictExists = (int)await conflictCommand.ExecuteScalarAsync();

    if (conflictExists > 0)
        return Conflict(new ErrorResponseDto { Message = "Doctor already has another appointment at this time" });

    await using var updateCommand = new SqlCommand(@"
        UPDATE dbo.Appointments
        SET IdPatient = @IdPatient,
            IdDoctor = @IdDoctor,
            AppointmentDate = @AppointmentDate,
            Status = @Status,
            Reason = @Reason,
            InternalNotes = @InternalNotes
        WHERE IdAppointment = @IdAppointment;
    ", connection);

    updateCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
    updateCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
    updateCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
    updateCommand.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
    updateCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
    updateCommand.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
        (object?)request.InternalNotes ?? DBNull.Value;
    updateCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = id;

    await updateCommand.ExecuteNonQueryAsync();

    return Ok(new ErrorResponseDto { Message = "Appointment updated successfully" });
}

    // DELETE /api/appointments/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAppointment(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // check status
        await using var checkCommand = new SqlCommand(@"
            SELECT Status FROM Appointments WHERE IdAppointment = @Id;
        ", connection);

        checkCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        var status = (string?)await checkCommand.ExecuteScalarAsync();

        if (status == null)
            return NotFound(new ErrorResponseDto { Message = "Not found" });

        if (status == "Completed")
            return Conflict(new ErrorResponseDto { Message = "Cannot delete completed appointment" });

        await using var deleteCommand = new SqlCommand(@"
            DELETE FROM Appointments WHERE IdAppointment = @Id;
        ", connection);

        deleteCommand.Parameters.Add("@Id", SqlDbType.Int).Value = id;

        await deleteCommand.ExecuteNonQueryAsync();

        return NoContent();
    }
}
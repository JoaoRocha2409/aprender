using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Configuração do CORS para permitir que o site.html faça requisições à API
builder.Services.AddCors(options =>
{
    options.AddPolicy("PermitirTudo", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// Configuração do SQLite com Entity Framework Core
builder.Services.AddDbContext<DeskTiDbContext>(options =>
    options.UseSqlite("Data Source=deskti.db"));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

var app = builder.Build();
app.UseCors("PermitirTudo");

// Inicializa o banco de dados e insere os dados iniciais (Seed) caso esteja vazio
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DeskTiDbContext>();
    db.Database.EnsureCreated();
    DatabaseSeeder.Seed(db);
}

// =========================================================================
// ENDPOINTS DE AUTENTICAÇÃO E USUÁRIOS
// =========================================================================

app.MapGet("/api/usuarios", async (DeskTiDbContext db) =>
    await db.Usuarios.ToListAsync());

app.MapPost("/api/login", async (LoginRequest req, DeskTiDbContext db) =>
{
    var user = await db.Usuarios
        .FirstOrDefaultAsync(u => u.Email == req.Email && u.Senha == req.Senha);
    
    return user is not null 
        ? Results.Ok(user) 
        : Results.Unauthorized();
});

app.MapPost("/api/usuarios", async (Usuario novoUsuario, DeskTiDbContext db) =>
{
    if (await db.Usuarios.AnyAsync(u => u.Email == novoUsuario.Email))
        return Results.BadRequest(new { mensagem = "Este e-mail já está em uso!" });

    db.Usuarios.Add(novoUsuario);
    await db.SaveChangesAsync();
    return Results.Created($"/api/usuarios/{novoUsuario.Id}", novoUsuario);
});

// =========================================================================
// ENDPOINTS DE CHAMADOS
// =========================================================================

// Listar todos os chamados com Solicitante, Técnico e Histórico
app.MapGet("/api/chamados", async (DeskTiDbContext db) =>
{
    var chamados = await db.Chamados
        .Include(c => c.Solicitante)
        .Include(c => c.TecnicoResponsavel)
        .Include(c => c.Historico)
        .OrderByDescending(c => c.Id)
        .ToListAsync();

    return Results.Ok(chamados);
});

// Abrir novo chamado
app.MapPost("/api/chamados", async (NovoChamadoRequest req, DeskTiDbContext db) =>
{
    var solicitante = await db.Usuarios.FindAsync(req.SolicitanteId);
    if (solicitante is null)
        return Results.BadRequest(new { mensagem = "Solicitante não encontrado." });

    var chamado = new Chamado
    {
        Titulo = req.Titulo,
        Descricao = req.Descricao,
        Categoria = req.Categoria,
        Prioridade = req.Prioridade,
        Status = "Aberto",
        SolicitanteId = solicitante.Id,
        DataAbertura = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
    };

    chamado.Historico.Add(new HistoricoRegistro
    {
        Data = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
        Descricao = $"Chamado aberto por {solicitante.Nome}"
    });

    db.Chamados.Add(chamado);
    await db.SaveChangesAsync();

    return Results.Created($"/api/chamados/{chamado.Id}", chamado);
});

// Atribuir Técnico (Administrador)
app.MapPut("/api/chamados/{id:int}/atribuir", async (int id, AtribuirTecnicoRequest req, DeskTiDbContext db) =>
{
    var chamado = await db.Chamados.Include(c => c.Historico).FirstOrDefaultAsync(c => c.Id == id);
    var tecnico = await db.Usuarios.FindAsync(req.TecnicoId);

    if (chamado is null || tecnico is null)
        return Results.NotFound(new { mensagem = "Chamado ou técnico não encontrado." });

    chamado.TecnicoId = tecnico.Id;
    if (chamado.Status == "Aberto")
        chamado.Status = "Em Atendimento";

    chamado.Historico.Add(new HistoricoRegistro
    {
        Data = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
        Descricao = $"Atribuído ao técnico {tecnico.Nome}"
    });

    await db.SaveChangesAsync();
    return Results.Ok(chamado);
});

// Atualizar Atendimento Técnico (Status e Solução)
app.MapPut("/api/chamados/{id:int}/atendimento", async (int id, AtualizarAtendimentoRequest req, DeskTiDbContext db) =>
{
    var chamado = await db.Chamados.Include(c => c.Historico).FirstOrDefaultAsync(c => c.Id == id);
    if (chamado is null) return Results.NotFound();

    if ((req.Status == "Resolvido" || req.Status == "Encerrado") && string.IsNullOrWhiteSpace(req.Solucao))
        return Results.BadRequest(new { mensagem = "É obrigatório informar a solução para encerrar o chamado!" });

    chamado.Status = req.Status;
    chamado.Solucao = req.Solucao;
    chamado.Historico.Add(new HistoricoRegistro
    {
        Data = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
        Descricao = $"Status modificado para [{req.Status}]. Apontamento: {req.Solucao}"
    });

    await db.SaveChangesAsync();
    return Results.Ok(chamado);
});

// Reabrir Chamado (Solicitante)
app.MapPut("/api/chamados/{id:int}/reabrir", async (int id, DeskTiDbContext db) =>
{
    var chamado = await db.Chamados.Include(c => c.Historico).FirstOrDefaultAsync(c => c.Id == id);
    if (chamado is null) return Results.NotFound();

    chamado.Status = "Em Atendimento";
    chamado.Historico.Add(new HistoricoRegistro
    {
        Data = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
        Descricao = "Solicitação de REABERTURA efetuada pelo cliente."
    });

    await db.SaveChangesAsync();
    return Results.Ok(chamado);
});

// Avaliar Atendimento (Solicitante)
app.MapPut("/api/chamados/{id:int}/avaliar", async (int id, AvaliacaoRequest req, DeskTiDbContext db) =>
{
    var chamado = await db.Chamados.Include(c => c.Historico).FirstOrDefaultAsync(c => c.Id == id);
    if (chamado is null) return Results.NotFound();

    chamado.Avaliacao = req.Nota;
    chamado.Historico.Add(new HistoricoRegistro
    {
        Data = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
        Descricao = $"Atendimento classificado com a nota: {req.Nota}/5"
    });

    await db.SaveChangesAsync();
    return Results.Ok(chamado);
});

// Excluir Chamado Resolvido (Permissão exclusiva para Técnico de TI e Administrador)
app.MapDelete("/api/chamados/{id:int}", async (int id, int usuarioId, DeskTiDbContext db) =>
{
    var usuario = await db.Usuarios.FindAsync(usuarioId);
    if (usuario is null || (usuario.Tipo != "Tecnico" && usuario.Tipo != "Administrador"))
        return Results.Forbid();

    var chamado = await db.Chamados.Include(c => c.Historico).FirstOrDefaultAsync(c => c.Id == id);
    if (chamado is null)
        return Results.NotFound(new { mensagem = "Chamado não encontrado." });

    if (chamado.Status != "Resolvido")
        return Results.BadRequest(new { mensagem = "Apenas chamados com status 'Resolvido' podem ser excluídos!" });

    db.Chamados.Remove(chamado);
    await db.SaveChangesAsync();

    return Results.Ok(new { mensagem = $"Chamado #{id} excluído com sucesso!" });
});

app.Run("http://localhost:5000");

// =========================================================================
// ENTIDADES E CONTEXTO DO ENTITY FRAMEWORK CORE
// =========================================================================

public class Usuario
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Telefone { get; set; } = string.Empty;
    public string Setor { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;
    public string Tipo { get; set; } = "Usuario"; // 'Usuario', 'Tecnico', 'Administrador'
}

public class Chamado
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string Categoria { get; set; } = string.Empty;
    public string Prioridade { get; set; } = string.Empty;
    public string Status { get; set; } = "Aberto";
    public int SolicitanteId { get; set; }
    public Usuario? Solicitante { get; set; }
    public int? TecnicoId { get; set; }
    public Usuario? TecnicoResponsavel { get; set; }
    public string DataAbertura { get; set; } = string.Empty;
    public string Solucao { get; set; } = string.Empty;
    public int? Avaliacao { get; set; }
    public List<HistoricoRegistro> Historico { get; set; } = new();
}

public class HistoricoRegistro
{
    public int Id { get; set; }
    public int ChamadoId { get; set; }
    public string Data { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
}

public record LoginRequest(string Email, string Senha);
public record NovoChamadoRequest(string Titulo, string Descricao, string Categoria, string Prioridade, int SolicitanteId);
public record AtribuirTecnicoRequest(int TecnicoId);
public record AtualizarAtendimentoRequest(string Status, string Solucao);
public record AvaliacaoRequest(int Nota);

public class DeskTiDbContext : DbContext
{
    public DeskTiDbContext(DbContextOptions<DeskTiDbContext> options) : base(options) { }

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Chamado> Chamados => Set<Chamado>();
    public DbSet<HistoricoRegistro> Historicos => Set<HistoricoRegistro>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Usuario>().ToTable("usuarios");
        modelBuilder.Entity<Chamado>().ToTable("chamados")
            .HasOne(c => c.Solicitante)
            .WithMany()
            .HasForeignKey(c => c.SolicitanteId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Chamado>()
            .HasOne(c => c.TecnicoResponsavel)
            .WithMany()
            .HasForeignKey(c => c.TecnicoId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<HistoricoRegistro>().ToTable("historico_chamados")
            .HasOne<Chamado>()
            .WithMany(c => c.Historico)
            .HasForeignKey(h => h.ChamadoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public static class DatabaseSeeder
{
    public static void Seed(DeskTiDbContext db)
    {
        if (db.Usuarios.Any()) return;

        var admin = new Usuario { Id = 1, Nome = "Administrador do Sistema", Email = "admin@ti.com", Telefone = "000000000", Setor = "TI", Senha = "123", Tipo = "Administrador" };
        var tec = new Usuario { Id = 2, Nome = "Técnico Suporte", Email = "tec@ti.com", Telefone = "111111111", Setor = "TI", Senha = "123", Tipo = "Tecnico" };
        var user = new Usuario { Id = 3, Nome = "Usuário Teste", Email = "user@ti.com", Telefone = "222222222", Setor = "Financeiro", Senha = "123", Tipo = "Usuario" };

        db.Usuarios.AddRange(admin, tec, user);

        var c1 = new Chamado
        {
            Id = 101,
            Titulo = "Computador não liga após queda de energia",
            Descricao = "A máquina do setor financeiro apita 3 vezes e não dá vídeo.",
            Categoria = "Hardware",
            Prioridade = "Alta",
            Status = "Aberto",
            SolicitanteId = 3,
            DataAbertura = "01/11/2026 09:15:00"
        };
        c1.Historico.Add(new HistoricoRegistro { Data = "01/11/2026 09:15:00", Descricao = "Chamado aberto por Usuário Teste" });

        var c2 = new Chamado
        {
            Id = 102,
            Titulo = "Erro ao acessar pasta compartilhada da rede",
            Descricao = "Aparece mensagem de permissão negada no servidor de arquivos.",
            Categoria = "Redes",
            Prioridade = "Média",
            Status = "Em Atendimento",
            SolicitanteId = 3,
            TecnicoId = 2,
            DataAbertura = "02/11/2026 14:20:00",
            Solucao = "Verificando permissões no Active Directory."
        };
        c2.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 14:20:00", Descricao = "Chamado aberto por Usuário Teste" });
        c2.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 14:35:00", Descricao = "Atribuído ao técnico Técnico Suporte" });

        var c3 = new Chamado
        {
            Id = 103,
            Titulo = "Solicitação de redefinição de senha do ERP",
            Descricao = "Usuário bloqueou a conta após 3 tentativas incorretas.",
            Categoria = "Acessos",
            Prioridade = "Baixa",
            Status = "Resolvido",
            SolicitanteId = 3,
            TecnicoId = 2,
            DataAbertura = "02/11/2026 16:00:00",
            Solucao = "Senha resetada e enviada temporariamente por e-mail.",
            Avaliacao = 5
        };
        c3.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 16:00:00", Descricao = "Chamado aberto por Usuário Teste" });
        c3.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 16:10:00", Descricao = "Atribuído ao técnico Técnico Suporte" });
        c3.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 16:40:00", Descricao = "Status modificado para [Resolvido]. Apontamento: Senha resetada e enviada temporariamente por e-mail." });
        c3.Historico.Add(new HistoricoRegistro { Data = "02/11/2026 17:00:00", Descricao = "Atendimento classificado com a nota: 5/5" });

        db.Chamados.AddRange(c1, c2, c3);
        db.SaveChanges();
    }
}